using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("本地翻译")]
[assembly: AssemblyProduct("本地翻译")]
[assembly: AssemblyDescription("本地模型翻译")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyInformationalVersion("1.0.1")]

namespace LocalTranslate {
    internal static class App {
        [STAThread] public static void Main() {
            bool created;
            using (var mutex=new Mutex(true,@"Local\TranslateDesktop_47836",out created)) {
                if (!created) {
                    var current=Process.GetCurrentProcess();
                    foreach(var other in Process.GetProcessesByName(current.ProcessName)) {
                        if(other.Id==current.Id || other.MainWindowHandle==IntPtr.Zero) continue;
                        if(IsIconic(other.MainWindowHandle)) ShowWindow(other.MainWindowHandle,9);
                        SetForegroundWindow(other.MainWindowHandle); break;
                    }
                    return;
                }
                var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
                app.Resources.MergedDictionaries.Add(Load<ResourceDictionary>("Styles.xaml"));
                var main=new MainView();
                var icon=LoadIcon();
                main.Window.Icon=icon;
                main.Window.Get<Image>("BrandIcon").Source=icon;
                app.MainWindow=main.Window;
                app.Run(main.Window);
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int command);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        static ImageSource LoadIcon() {
            using(var data=Assembly.GetExecutingAssembly().GetManifestResourceStream("translate.ico")) {
                if(data==null) throw new UiException("error.icon");
                var decoder=BitmapDecoder.Create(data,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var icon=decoder.Frames.OrderByDescending(frame=>frame.PixelWidth).First();
                icon.Freeze();
                return icon;
            }
        }
        internal static T Load<T>(string file) {
            Ui.EnsureInitialized();
            using(var data=Assembly.GetExecutingAssembly().GetManifestResourceStream(file)) return (T)XamlReader.Load(data);
        }
        internal static T Get<T>(this Window window,string name) where T:class { return (T)window.FindName(name); }
        internal static async Task CopyText(string text,TextBlock status,string confirmation="status.copied") {
            if(String.IsNullOrEmpty(text)) return;
            for(int i=0;i<4;i++) {
                try { Clipboard.SetText(text); Say(status,confirmation); return; }
                catch(System.Runtime.InteropServices.COMException) { await Task.Delay(50); }
            }
            Say(status,"error.clipboard");
        }
        internal static string Message(Exception error) { return Ui.Error(error).ToString(); }
        internal static void Status(TextBlock label,UiText text) { Ui.Present(label,text); }
        internal static void Say(TextBlock label,string key,params object[] args) { Ui.Present(label,Ui.M(key,args)); }
    }

    internal sealed class StreamView {
        readonly TextBox result;
        readonly TextBlock status;
        readonly Button stop,copy;
        readonly Action<bool> busyChanged;
        CancellationTokenSource active;
        string id;
        internal StreamView(Window window,string statusName, Action<bool> busyChanged,string prefix="") {
            result=window.Get<TextBox>(prefix+"Result"); status=window.Get<TextBlock>(statusName);
            copy=window.Get<Button>(prefix+"Copy"); stop=window.Get<Button>(prefix+"Stop"); this.busyChanged=busyChanged;
            stop.Click+=(s,e)=>Cancel(); copy.Click+=async(s,e)=>await App.CopyText(result.Text,status);
            result.TextChanged+=(s,e)=>copy.IsEnabled=result.Text.Length>0;
        }
        internal void Cancel() {
            if(active==null) return;
            var handle=id; var source=active;
            active=null; id=null; source.Cancel(); Signal(false);
            App.Say(status,"status.stopped"); SignalCancel(handle);
        }
        internal static async void SignalCancel(string handle) {
            try { await Api.Call("/api/cancel",new {id=handle},2500); } catch(Exception) { }
        }
        void Signal(bool busy) {
            stop.Visibility=busy ? Visibility.Visible : Visibility.Collapsed;
            if(busyChanged!=null) busyChanged(busy);
        }
        internal async Task Run(Model model,Direction direction,string source,Action started=null) {
            Cancel();
            if(model==null || String.IsNullOrWhiteSpace(source)) { App.Say(status,"status.need_text"); return; }
            if(direction==null) { App.Say(status,"status.wait_languages"); return; }
            if(!model.Available) { App.Status(status,String.IsNullOrEmpty(model.Reason) ? Ui.M("status.unavailable") : Ui.Diagnostic(model.Reason)); return; }
            var token=new CancellationTokenSource();
            id=Guid.NewGuid().ToString("N"); active=token; result.Clear(); Signal(true);
            if(started!=null) started();
            App.Say(status,"status.translating");
            try {
                await Api.Translate(id,model.Id,source,direction,(value,final)=>{
                    if(active!=token) return;
                    if(final) result.Text=value;
                    else result.AppendText(value);
                },token.Token);
                if(active==token) App.Say(status,"status.translated",model.Name);
            } catch(Exception err) {
                if(active==token) App.Status(status,Ui.Error(err));
            } finally {
                if(active==token) { active=null; id=null; Signal(false); }
                token.Dispose();
            }
        }
    }

    internal sealed partial class MainView {
        internal readonly Window Window=App.Load<Window>("Main.xaml");
        readonly Options options=Options.Read();
        readonly ComboBox models,sourceLanguage,targetLanguage;
        readonly ToggleButton capture;
        readonly TextBlock health;
        readonly SelectionService selection;
        readonly StreamView translator;
        readonly FileView files;
        readonly ImageView images;
        readonly ChatView chat;
        readonly ModelManagerView modelManager;
        Model[] catalog={
            new Model{Id="gemma4b",Name="Gemma 4B",Available=true},
            new Model{Id="index2b",Name="Index 2B",Available=true},
            new Model{Id="hymt7b",Name="HY-MT 7B",Available=true}
        };
        Window popup;
        Process server;
        bool updating,closing,shutdownReady;
        internal MainView() {
            health=Window.Get<TextBlock>("Health"); models=Window.Get<ComboBox>("Models");
            sourceLanguage=Window.Get<ComboBox>("SourceLanguage"); targetLanguage=Window.Get<ComboBox>("TargetLanguage");
            EnableLanguageLookup(sourceLanguage); EnableLanguageLookup(targetLanguage);
            capture=Window.Get<ToggleButton>("CaptureSwitch"); selection=new SelectionService(Window,OpenSelection);
            FillModels();
            models.SelectionChanged+=(s,e)=>{
                if(updating || Selected==null) return;
                string source=options.source_lang,target=options.target_lang;
                options.model_id=Selected.Id; FillLanguages(); Save();
                var changed=new List<UiText>();
                if(source!=options.source_lang) changed.Add(Ui.M("common.source_changed",sourceLanguage.SelectedItem));
                if(target!=options.target_lang) changed.Add(Ui.M("common.target_changed",targetLanguage.SelectedItem));
                RefreshAvailability(Ui.Join(" · ",changed.ToArray()));
            };
            sourceLanguage.SelectionChanged+=(s,e)=>StoreLanguages();
            targetLanguage.SelectionChanged+=(s,e)=>StoreLanguages();
            var languagePopup=Window.Get<Popup>("LanguagePopup");
            Window.Get<Button>("ShowLanguages").Click+=(s,e)=>languagePopup.IsOpen=true;
            languagePopup.PreviewKeyDown+=(s,e)=>{ if(e.Key==System.Windows.Input.Key.Escape)languagePopup.IsOpen=false; };
            capture.Checked+=(s,e)=>ChangeSelection(true); capture.Unchecked+=(s,e)=>ChangeSelection(false);
            capture.IsChecked=options.selection_enabled;
            var src=Window.Get<TextBox>("Source"); var run=Window.Get<Button>("Translate"); var hint=Window.Get<TextBlock>("SourceHint");
            translator=new StreamView(Window,"TextStatus",busy=>{ run.Visibility=busy ? Visibility.Collapsed : Visibility.Visible; });
            run.IsEnabled=false;
            src.TextChanged+=(s,e)=>{ run.IsEnabled=!String.IsNullOrWhiteSpace(src.Text); hint.Visibility=src.Text.Length==0 ? Visibility.Visible : Visibility.Collapsed; };
            run.Click+=async(s,e)=>await translator.Run(Selected,Language,src.Text);
            images=new ImageView(Window,()=>Selected,()=>Language);
            files=new FileView(Window,()=>Selected,()=>Language,images);
            chat=new ChatView(Window);
            modelManager=new ModelManagerView(Window,ReloadModels);
            SetupLanguageSettings();
            var outputMode=Window.Get<ComboBox>("FileMode");
            outputMode.SelectedIndex=options.bilingual ? 1 : 0;
            outputMode.SelectionChanged+=(s,e)=>{ options.bilingual=outputMode.SelectedIndex==1; Save(); };
            Window.Loaded+=async(s,e)=>{ await Connect(); };
            Window.Closing+=async(s,e)=>{
                if(shutdownReady) return;
                e.Cancel=true; if(closing) return; closing=true;
                modelManager.Close();
                var selectionClosed=selection.Close(); if(popup!=null)popup.Close(); translator.Cancel();
                await Task.WhenAll(selectionClosed,files.Close(),chat.Close(),images.Close());
                if(server!=null) {
                    try { if(!server.HasExited) await Api.Call("/api/shutdown",new {},3000); } catch(Exception) { }
                    server.Dispose();
                }
                await System.Windows.Threading.Dispatcher.Yield();
                shutdownReady=true; Window.Close();
            };
        }
        Model Selected { get { return models.SelectedItem as Model; } }
        Direction Language {
            get {
                var source=sourceLanguage.SelectedItem as LanguageOption; var target=targetLanguage.SelectedItem as LanguageOption;
                return source==null || target==null ? null : new Direction {Source=source.Code,Target=target.Code};
            }
        }
        void StoreLanguages() {
            var lang=Language;
            if(updating || lang==null) return;
            options.source_lang=lang.Source; options.target_lang=lang.Target; Save(); RefreshAvailability();
        }
        static void EnableLanguageLookup(ComboBox picker) {
            string typed="";
            var interval=new Stopwatch();
            picker.IsTextSearchEnabled=false;
            if(picker.ToolTip==null && !BindingOperations.IsDataBound(picker,FrameworkElement.ToolTipProperty))
                picker.SetResourceReference(FrameworkElement.ToolTipProperty,"common.language_lookup");
            picker.DropDownOpened+=(s,e)=>{ typed=""; interval.Reset(); };
            picker.PreviewTextInput+=(s,e)=>{
                if(String.IsNullOrWhiteSpace(e.Text)) return;
                string query=interval.IsRunning && interval.ElapsedMilliseconds<1300 ? typed+e.Text : e.Text;
                var choices=picker.Items.OfType<LanguageOption>().ToList();
                var match=MatchLanguage(choices,query);
                if(match==null) { query=e.Text; match=MatchLanguage(choices,query); }
                typed=query; interval.Restart(); e.Handled=true;
                if(match==null) return;
                picker.SelectedItem=match;
                var item=picker.ItemContainerGenerator.ContainerFromItem(match) as ComboBoxItem;
                if(item!=null) item.BringIntoView();
            };
        }
        static LanguageOption MatchLanguage(List<LanguageOption> choices,string query) {
            return choices.FirstOrDefault(x=>String.Equals(x.Code,query,StringComparison.OrdinalIgnoreCase))
                ?? choices.FirstOrDefault(x=>x.Name.StartsWith(query,StringComparison.CurrentCultureIgnoreCase) || x.Code.StartsWith(query,StringComparison.OrdinalIgnoreCase));
        }
        void FillLanguages() {
            var model=Selected;
            if(model==null) return;
            updating=true;
            sourceLanguage.ItemsSource=model.Sources; targetLanguage.ItemsSource=model.Targets;
            sourceLanguage.SelectedItem=model.Sources.FirstOrDefault(x=>x.Code==options.source_lang) ?? model.Sources.FirstOrDefault(x=>x.Code=="auto") ?? model.Sources.FirstOrDefault();
            targetLanguage.SelectedItem=model.Targets.FirstOrDefault(x=>x.Code==options.target_lang) ?? model.Targets.FirstOrDefault(x=>x.Code=="zh") ?? model.Targets.FirstOrDefault();
            var lang=Language;
            if(lang!=null) { options.source_lang=lang.Source; options.target_lang=lang.Target; }
            Window.Get<Button>("ShowLanguages").IsEnabled=lang!=null;
            UpdateLanguageList();
            updating=false;
        }
        void UpdateLanguageList() {
            var model=Selected; if(model==null) return;
            string separator=Ui.T("common.list_separator");
            var sourceNames=model.Sources.Where(x=>x.Code!="auto").Select(x=>x.Name).ToArray();
            var targetNames=model.Targets.Select(x=>x.Name).ToArray();
            Window.Get<TextBlock>("LanguageTitle").Text=Ui.T("common.language_list",model.Name);
            Window.Get<TextBlock>("LanguageList").Text=sourceNames.SequenceEqual(targetNames) ? String.Join(separator,targetNames)
                : Ui.T("common.source_list",String.Join(separator,sourceNames))+"\n"+Ui.T("common.target_list",String.Join(separator,targetNames));
        }
        static LanguageOption[] ReadLanguages(Dictionary<string,object> row,string key) {
            return Api.Rows(row,key).Select(x=>new LanguageOption {Code=Api.Value(x,"code"),Name=Api.Value(x,"name")})
                .Where(x=>x.Code.Length>0 && x.Name.Length>0).ToArray();
        }
        void FillModels() {
            updating=true; models.ItemsSource=catalog;
            models.SelectedItem=catalog.FirstOrDefault(m=>m.Id==options.model_id)??catalog[0];
            FillLanguages(); updating=false;
        }
        void Save() {
            try { options.Save(); } catch(Exception err) { App.Say(health,"status.save_failed",Ui.Error(err)); }
        }
        void UpdateModelTooltip() {
            var model=Selected;
            models.ToolTip=model==null ? null : model.Name+(String.IsNullOrWhiteSpace(model.Reason) ? "" : " · "+Ui.Diagnostic(model.Reason));
        }
        void RefreshAvailability(UiText change=null) {
            var model=Selected;
            UpdateModelTooltip();
            if(!Api.Connected)return;
            UiText message=model!=null && !model.Available ? Ui.M("status.model_unavailable",Ui.Diagnostic(model.Reason)) : Ui.M("status.connected");
            App.Status(health,Ui.Join(" · ",message,change));
            if(model!=null && model.Available && model.Sources.Length==0) App.Say(health,"status.no_languages");
            Window.Get<System.Windows.Shapes.Ellipse>("HealthDot").Fill=new SolidColorBrush((Color)ColorConverter.ConvertFromString(model!=null && !model.Available ? "#A27323" : "#096D63"));
        }
        async Task Connect() {
            try {
                Dictionary<string,object> initial=null;
                try { initial=await Api.Call("/api/health",timeout:900); }
                catch(Exception) { if(closing)return; server=Api.Launch(); }
                if(initial!=null) VerifyService(initial);
                Exception last=null;
                for(int i=0;i<45 && !closing;i++) {
                    try {
                        VerifyService(await Api.Call("/api/health",timeout:1500));
                        await ReloadModels();
                        if(closing)return;
                        modelManager.ServiceReady(); chat.ServiceReady(); await images.LoadCapabilities(); return;
                    } catch(Exception err) { last=err; }
                    if(server!=null && server.HasExited) throw new UiException("error.service_start");
                    await Task.Delay(350);
                }
                if(!closing && last!=null) throw last;
            } catch(Exception err) { if(!closing) App.Status(health,Ui.Error(err)); }
        }
        async Task ReloadModels() {
            var data=await Api.Call("/api/models",timeout:5000);
            if(closing)return;
            var loaded=Api.Rows(data,"models").Select(r=>new Model {
                Id=Api.Value(r,"id"),Name=Api.Value(r,"name"),Available=String.Equals(Api.Value(r,"available"),"true",StringComparison.OrdinalIgnoreCase),Reason=Api.Value(r,"reason"),
                Sources=ReadLanguages(r,"source_languages"),Targets=ReadLanguages(r,"target_languages")
            }).Where(m=>m.Id.Length>0).ToArray();
            if(loaded.Length>0) { catalog=loaded; FillModels(); }
            RefreshAvailability();
        }
        internal static void VerifyService(Dictionary<string,object> healthData) {
            if(Api.Value(healthData,"service")!="local-translator-v1")
                throw new UiException("error.port");
            Api.Connected=true;
        }
        void ChangeSelection(bool enabled) {
            if(enabled) {
                try { selection.Start(); }
                catch(Exception err) { capture.IsChecked=false; App.Say(health,"selection.failed",Ui.Error(err)); return; }
            } else { selection.Dispose(); if(popup!=null) popup.Close(); }
            options.selection_enabled=enabled; Save();
        }
        static void BindPopupPicker(ComboBox picker,ComboBox home) {
            picker.SetBinding(ItemsControl.ItemsSourceProperty,new Binding("ItemsSource"){Source=home,Mode=BindingMode.OneWay});
            picker.SetBinding(Selector.SelectedItemProperty,new Binding("SelectedItem"){Source=home,Mode=BindingMode.TwoWay});
        }
        async void OpenSelection(string text,Native.PointI point) {
            if(closing || capture.IsChecked!=true) return;
            if(popup!=null) popup.Close();
            var panel=App.Load<Window>("Popup.xaml"); popup=panel;
            var picker=panel.Get<ComboBox>("Models");
            var input=panel.Get<ComboBox>("SourceLanguage"); var output=panel.Get<ComboBox>("TargetLanguage");
            BindPopupPicker(picker,models); BindPopupPicker(input,sourceLanguage); BindPopupPicker(output,targetLanguage);
            EnableLanguageLookup(input); EnableLanguageLookup(output);
            string original=text.Replace("\r"," ").Replace("\n"," ");
            Ui.Bind(panel.Get<TextBlock>("Original"),TextBlock.TextProperty,selection.LastReadByOcr ? Ui.M("selection.screen",original) : (UiText)original);
            var translation=new StreamView(panel,"Status",null);
            var label=panel.Get<TextBlock>("ResultLanguages");
            var result=panel.Get<TextBox>("Result");
            int detectionVersion=0;
            void ClearDetection() {
                detectionVersion++;
                label.Text=""; label.Visibility=Visibility.Collapsed;
            }
            async Task DetectInput(int version) {
                try {
                    var data=await Api.Call("/api/detect-language",new {text},3500);
                    var name=Api.Value(data,"name");
                    if(popup==panel && version==detectionVersion && (input.SelectedItem as LanguageOption)?.Code=="auto" &&
                        !String.IsNullOrWhiteSpace(name) && !String.IsNullOrWhiteSpace(Api.Value(data,"code"))) {
                        var code=Api.Value(data,"code");
                        App.Say(label,"selection.detected",new UiText(()=>Ui.Language(code,name))); label.Visibility=Visibility.Visible;
                    }
                } catch(Exception) { } // The translation can continue with automatic input.
            }
            Task TranslateSelection() {
                var model=picker.SelectedItem as Model;
                var source=input.SelectedItem as LanguageOption; var target=output.SelectedItem as LanguageOption;
                var direction=source==null || target==null ? null : new Direction { Source=source.Code,Target=target.Code };
                return translation.Run(model,direction,text,()=>{
                    ClearDetection();
                    Ui.Bind(result,FrameworkElement.ToolTipProperty,Ui.M("selection.direction",source,target));
                    if(source.Code=="auto") _=DetectInput(detectionVersion);
                });
            }
            input.SelectionChanged+=(s,e)=>ClearDetection();
            panel.Get<Button>("Again").Click+=async(s,e)=>await TranslateSelection();
            panel.Get<Button>("ClosePopup").Click+=(s,e)=>panel.Close();
            panel.Closed+=(s,e)=>{
                if(popup==panel) popup=null;
                translation.Cancel();
            };
            Native.Place(panel,point,false); panel.Show(); Native.Place(panel,point,false);
            SelectionDiagnostics.Record("popup.shown");
            await TranslateSelection();
        }
    }
}
