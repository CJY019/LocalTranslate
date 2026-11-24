using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Controls;

namespace LocalTranslate {
    internal sealed class UiChoice : INotifyPropertyChanged {
        internal string Code,NativeName;
        internal LanguagePack Pack;
        internal bool Cached;
        internal string Name { get { return Code=="system" ? Ui.T("settings.system") : NativeName; } }
        public string Display { get {
            if(Code=="system") return Name;
            return Ui.T("settings.choice",NativeName,Ui.T(Pack==null ? "settings.builtin" : Cached ? "settings.cached" : "settings.download"));
        } }
        internal void Refresh() { PropertyChanged?.Invoke(this,new PropertyChangedEventArgs("Display")); }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed partial class MainView {
        void SetupLanguageSettings() {
            var picker=Window.Get<ComboBox>("UiLanguage");
            var status=Window.Get<TextBlock>("UiLanguageStatus");
            var choices=new List<UiChoice> {
                new UiChoice { Code="system" },
                new UiChoice { Code="zh-CN",NativeName="简体中文" },
                new UiChoice { Code="en-US",NativeName="English" }
            };
            choices.AddRange(Ui.Packs.Select(p=>new UiChoice { Code=p.Locale,NativeName=p.NativeName,Pack=p,Cached=Ui.HasCache(p) }));
            picker.ItemsSource=choices;
            picker.SelectedItem=Ui.StartupError==null ? choices.FirstOrDefault(c=>c.Code==options.ui_language)??choices[0] : choices[0];
            if(Ui.StartupError!=null) App.Say(status,"settings.cache_error",Ui.StartupError);
            bool switching=false;
            var lifetime=new CancellationTokenSource();
            UiChoice previous=(UiChoice)picker.SelectedItem;
            picker.SelectionChanged+=async(s,e)=>{
                var requested=picker.SelectedItem as UiChoice;
                if(switching || requested==null || closing) return;
                switching=true; picker.IsEnabled=false;
                try {
                    if(requested.Pack!=null && !requested.Cached) App.Say(status,"settings.loading",requested.Name);
                    string locale=Ui.Effective(requested.Code);
                    var pack=await Ui.Prepare(locale,lifetime.Token);
                    if(closing || lifetime.IsCancellationRequested) return;
                    string old=options.ui_language;
                    options.ui_language=requested.Code;
                    try { options.Save(); } catch { options.ui_language=old; throw; }
                    requested.Cached=requested.Pack!=null; previous=requested;
                    Ui.Activate(locale,pack);
                    App.Say(status,"settings.applied",requested.Name);
                } catch(Exception err) {
                    if(!closing && !lifetime.IsCancellationRequested) {
                        picker.SelectedItem=previous;
                        App.Say(status,"settings.failed",Ui.Error(err));
                    }
                } finally { switching=false; if(!closing) picker.IsEnabled=true; }
            };
            Action refresh=()=>{
                foreach(var choice in choices) choice.Refresh();
                foreach(var model in catalog) {
                    model.RefreshText();
                    foreach(var language in model.Sources.Concat(model.Targets)) language.RefreshText();
                }
                UpdateLanguageList(); UpdateModelTooltip();
                files.RefreshLanguage(); chat.RefreshLanguage(); images.RefreshLanguage();
            };
            Ui.Changed+=refresh;
            Window.Closing+=(s,e)=>{ if(!lifetime.IsCancellationRequested) lifetime.Cancel(); };
            Window.Closed+=(s,e)=>{ Ui.Changed-=refresh; lifetime.Dispose(); };
        }
    }
}
