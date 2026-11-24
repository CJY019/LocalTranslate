using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LocalTranslate {
    internal sealed class ChatBubble : INotifyPropertyChanged {
        public bool IsUser { get; private set; }
        string modelName;
        public string Author { get { return IsUser ? Ui.T("chat.you") : Ui.T("chat.author",modelName); } }
        public string Text { get; private set; }
        string statusKey;
        public string Status { get { return String.IsNullOrEmpty(statusKey) ? "" : Ui.T(statusKey); } }
        public bool CanCopy { get { return !IsUser && !String.IsNullOrEmpty(Text); } }
        public event PropertyChangedEventHandler PropertyChanged;

        internal ChatBubble(bool user,string text) { IsUser=user; Text=text; }
        internal void UseModel(string name) { modelName=name; Changed("Author"); }
        internal void Write(string text) {
            bool hadText=CanCopy;
            Text=text;
            Changed("Text");
            if(hadText!=CanCopy) Changed("CanCopy");
        }
        internal void Mark(string key) { statusKey=key; Changed("Status"); }
        internal void RefreshLanguage() { Changed("Author"); Changed("Status"); }
        void Changed(string name) {
            if(PropertyChanged!=null) PropertyChanged(this,new PropertyChangedEventArgs(name));
        }
    }

    internal sealed class ChatView {
        readonly Window window;
        readonly ComboBox models;
        readonly TabItem tab;
        readonly TextBox input;
        readonly TextBlock hint,status;
        readonly Button send,stop,clear,retry,reconnect;
        readonly ObservableCollection<ChatBubble> bubbles=new ObservableCollection<ChatBubble>();
        readonly List<object> history=new List<object>();
        readonly ScrollViewer scroll;
        Model selected=new Model {Id="qwen35_9b",Name="Qwen3.5 9B"};
        CancellationTokenSource active;
        Task cancelSignals=Task.CompletedTask;
        string requestId,lastQuestion;
        ChatBubble lastReply;
        UiText catalogError;
        bool loading,hasCatalog,closing,canRetry,follow=true,composing;

        internal ChatView(Window window) {
            this.window=window;
            models=window.Get<ComboBox>("ChatModel");
            tab=window.Get<TabItem>("ChatTab");
            input=window.Get<TextBox>("ChatInput");
            hint=window.Get<TextBlock>("ChatHint");
            status=window.Get<TextBlock>("ChatStatus");
            send=window.Get<Button>("ChatSend"); stop=window.Get<Button>("ChatStop");
            clear=window.Get<Button>("ChatClear"); retry=window.Get<Button>("ChatRetry");
            reconnect=window.Get<Button>("ChatReconnect");
            scroll=window.Get<ScrollViewer>("ChatScroll");
            var messages=window.Get<ItemsControl>("ChatMessages");
            messages.ItemsSource=bubbles;
            messages.AddHandler(ButtonBase.ClickEvent,new RoutedEventHandler(async(s,e)=>{
                var button=e.OriginalSource as Button;
                var bubble=button==null ? null : button.DataContext as ChatBubble;
                if(bubble!=null && bubble.CanCopy) await App.CopyText(bubble.Text,status,"chat.copied");
            }));
            models.ItemsSource=new[]{selected}; models.SelectedIndex=0;
            models.SelectionChanged+=(s,e)=>{
                var choice=models.SelectedItem as Model;
                if(choice==null || loading) return;
                selected=choice; ShowAvailability(); Update();
            };
            var workspace=window.Get<TabControl>("Workspace");
            workspace.SelectionChanged+=(s,e)=>{
                if(e.OriginalSource!=workspace) return;
                bool show=tab.IsSelected;
                bool settings=window.Get<TabItem>("SettingsTab").IsSelected;
                window.Get<FrameworkElement>("OptionsBar").Visibility=Visible(!settings);
                window.Get<FrameworkElement>("TranslationOptions").Visibility=Visible(!show && !settings);
                window.Get<FrameworkElement>("ChatOptions").Visibility=Visible(show);
                window.Get<FrameworkElement>("TranslationFooter").Visibility=Visible(!show && !settings);
                window.Get<FrameworkElement>("ChatFooter").Visibility=Visible(show);
                if(show && !selected.Available) LoadModels();
            };
            input.TextChanged+=(s,e)=>Update();
            TextCompositionManager.AddPreviewTextInputStartHandler(input,(s,e)=>composing=true);
            TextCompositionManager.AddPreviewTextInputHandler(input,(s,e)=>composing=false);
            input.LostKeyboardFocus+=(s,e)=>composing=false;
            input.PreviewKeyDown+=async(s,e)=>{
                if(e.Key!=Key.Enter || composing || Keyboard.Modifiers!=ModifierKeys.None) return;
                e.Handled=true;
                if(!e.IsRepeat && send.IsEnabled) await Submit(false);
            };
            send.Click+=async(s,e)=>await Submit(false);
            stop.Click+=(s,e)=>Stop();
            clear.Click+=(s,e)=>{
                Stop(); history.Clear(); bubbles.Clear(); lastQuestion=null; lastReply=null; canRetry=false;
                input.Clear(); follow=true;
                ShowAvailability(); Update();
            };
            retry.Click+=async(s,e)=>await Submit(true);
            reconnect.Click+=(s,e)=>LoadModels();
            scroll.ScrollChanged+=(s,e)=>{
                if(e.ExtentHeightChange==0 && e.VerticalChange!=0) follow=scroll.ScrollableHeight-scroll.VerticalOffset<=12;
                if(e.ExtentHeightChange>0 && follow) scroll.ScrollToEnd();
            };
            Update();
        }

        static Visibility Visible(bool show) { return show ? Visibility.Visible : Visibility.Collapsed; }
        internal void RefreshLanguage() { foreach(var bubble in bubbles) bubble.RefreshLanguage(); }
        void Update() {
            bool busy=active!=null;
            hint.Visibility=Visible(input.Text.Length==0);
            send.Visibility=Visible(!busy); stop.Visibility=Visible(busy);
            send.IsEnabled=!closing && !loading && !busy && selected.Available && !String.IsNullOrWhiteSpace(input.Text);
            clear.IsEnabled=bubbles.Count!=0 || input.Text.Length!=0;
            retry.Visibility=Visible(canRetry && !busy);
            retry.IsEnabled=!closing && !loading && selected.Available;
            reconnect.Visibility=Visible(!selected.Available && !loading);
            models.IsEnabled=hasCatalog && !busy && !loading;
        }
        void ShowAvailability() {
            App.Status(status,selected.Available ? Ui.M("chat.ready") : catalogError ??
                (String.IsNullOrWhiteSpace(selected.Reason) ? Ui.M(hasCatalog?"error.chat_unavailable":"chat.connecting") : Ui.Diagnostic(selected.Reason)));
        }
        internal void ServiceReady() { if(tab.IsSelected && !selected.Available) LoadModels(); }
        async void LoadModels() {
            if(loading || closing) return;
            loading=true; catalogError=null; App.Say(status,"chat.connecting"); Update();
            try {
                if(!Api.Connected) MainView.VerifyService(await Api.Call("/api/health",timeout:2500));
                var data=await Api.Call("/api/chat/models",timeout:4000);
                var options=Api.Rows(data,"models").Select(row=>new Model {
                    Id=Api.Value(row,"id"),Name=Api.Value(row,"name",Api.Value(row,"id")),
                    Available=String.Equals(Api.Value(row,"available"),"true",StringComparison.OrdinalIgnoreCase),
                    Reason=Api.Value(row,"reason")
                }).Where(model=>!String.IsNullOrEmpty(model.Id)).ToArray();
                if(options.Length==0) throw new UiException("error.chat_models");
                if(closing) return;
                selected=options.FirstOrDefault(m=>m.Id==selected.Id && m.Available)
                    ?? options.FirstOrDefault(m=>m.Available) ?? options[0];
                models.ItemsSource=options; models.SelectedItem=selected; hasCatalog=true;
            } catch(Exception err) {
                hasCatalog=false; selected.Available=false; catalogError=Ui.Error(err);
            } finally {
                loading=false;
                if(!closing) {
                    ShowAvailability(); Update();
                }
            }
        }
        async Task Submit(bool again) {
            if(active!=null || closing || !selected.Available || loading) return;
            if(again && !canRetry) return;
            string question=again ? lastQuestion : input.Text.Trim();
            if(String.IsNullOrWhiteSpace(question)) return;
            if(!again) {
                lastQuestion=question; lastReply=new ChatBubble(false,"");
                bubbles.Add(new ChatBubble(true,question)); bubbles.Add(lastReply); input.Clear();
            }
            var reply=lastReply;
            reply.UseModel(selected.Name); reply.Write(""); reply.Mark("chat.generating");
            canRetry=false; follow=true; scroll.ScrollToEnd();
            var pending=history.Concat(new object[]{new {role="user",content=question}}).ToArray();
            var token=new CancellationTokenSource();
            string id=Guid.NewGuid().ToString("N"); active=token; requestId=id;
            App.Say(status,"chat.loading"); Update();
            try {
                await cancelSignals;
                token.Token.ThrowIfCancellationRequested();
                await Api.Chat(id,selected.Id,pending,(value,final)=>{
                    if(active!=token || closing) return;
                    if(reply.Text.Length==0 && value.Length!=0) { reply.Mark(""); App.Say(status,"chat.replying"); }
                    reply.Write(final ? value : reply.Text+value);
                },token.Token);
                if(active!=token || closing) return;
                if(String.IsNullOrWhiteSpace(reply.Text)) throw new UiException("error.chat_empty");
                history.Add(new {role="user",content=question}); history.Add(new {role="assistant",content=reply.Text});
                reply.Mark(""); ShowAvailability();
            } catch(Exception err) {
                if(active==token && !closing) {
                    canRetry=true; reply.Mark("chat.incomplete"); App.Status(status,Ui.Error(err));
                }
            } finally {
                if(active==token) { active=null; requestId=null; Update(); }
                token.Dispose();
            }
        }
        void Stop() {
            if(active==null) return;
            var token=active; string id=requestId; active=null; requestId=null;
            token.Cancel();
            cancelSignals=Task.WhenAll(cancelSignals,SignalCancel(id));
            canRetry=true; lastReply.Mark("status.stopped");
            App.Say(status,"status.stopped"); Update();
        }
        static async Task SignalCancel(string id) {
            try { await Api.Call("/api/cancel",new {id},2500); } catch(Exception) { }
        }
        internal Task Close() {
            closing=true; Stop(); return cancelSignals;
        }
    }
}
