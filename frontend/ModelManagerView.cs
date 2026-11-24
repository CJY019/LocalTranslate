using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LocalTranslate {
    internal sealed class ModelManagerView {
        readonly Window window;
        readonly StackPanel panel=new StackPanel(), rows=new StackPanel();
        readonly TextBlock heading=new TextBlock { FontSize=18,FontWeight=FontWeights.SemiBold };
        readonly TextBlock directory=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,6) };
        readonly TextBlock message=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0) };
        readonly Button reload=new Button();
        readonly DispatcherTimer timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(2) };
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly Dictionary<string,ModelRow> controls=new Dictionary<string,ModelRow>();
        readonly Func<Task> reloadCatalog;
        List<Dictionary<string,object>> models=new List<Dictionary<string,object>>();
        string token, catalogState;
        bool polling,active,ready,closed;

        internal ModelManagerView(Window window,Func<Task> reloadCatalog) {
            this.window=window; this.reloadCatalog=reloadCatalog;
            var header=new DockPanel();
            DockPanel.SetDock(reload,Dock.Right);
            header.Children.Add(reload); header.Children.Add(heading);
            panel.Children.Add(header); panel.Children.Add(directory); panel.Children.Add(rows); panel.Children.Add(message);
            window.Get<ContentControl>("ModelManagerHost").Content=panel;
            reload.Click+=async(s,e)=>await Refresh();
            timer.Tick+=async(s,e)=>await Refresh();
            panel.IsVisibleChanged+=(s,e)=>{ if(ready && panel.IsVisible) _=Refresh(); else Schedule(); };
            Ui.Changed+=Redraw; Redraw();
        }
        static bool Flag(Dictionary<string,object> row,string key) {
            object value; return row.TryGetValue(key,out value) && value is bool && (bool)value;
        }
        static long Bytes(Dictionary<string,object> row,string key) {
            long n; return Int64.TryParse(Api.Value(row,key),out n) ? n : 0;
        }
        static string Size(long bytes) { return (bytes/(1024.0*1024*1024)).ToString("0.00",CultureInfo.CurrentCulture)+" GiB"; }
        internal void ServiceReady() { ready=true; _=Refresh(); }
        internal void Close() { closed=true; timer.Stop(); Ui.Changed-=Redraw; lifetime.Cancel(); }
        void Schedule() {
            if(!closed && ready && (active || panel.IsVisible)) timer.Start(); else timer.Stop();
        }
        async Task Refresh() {
            if(!ready || closed || polling) return;
            polling=true;
            try {
                var data=await Api.Call("/api/model-manager",timeout:6000,token:lifetime.Token);
                if(closed) return;
                token=Api.Value(data,"token");
                models=Api.Rows(data,"models").ToList();
                // Poll offscreen only while an install can change availability in the main pickers.
                active=models.Any(row=>{
                    string status=Api.Value(row,"status");
                    return status=="downloading" || status=="importing" || status=="verifying" || status=="deleting" || status=="cancelling";
                });
                string state=String.Join("|",models.Select(row=>Api.Value(row,"id")+":"+Api.Value(row,"available")));
                bool availabilityChanged=state!=catalogState;
                if(panel.IsVisible) Redraw();
                if(availabilityChanged) {
                    await reloadCatalog();
                    catalogState=state;
                }
                if(!closed) message.Text=Ui.Diagnostic(Api.Value(data,"catalog_error")).ToString();
            } catch(Exception error) {
                if(!closed) message.Text=Ui.T("models.error",App.Message(error));
            } finally {polling=false;Schedule();}
        }
        void Redraw() {
            heading.Text=Ui.T("models.heading"); reload.Content=Ui.T("models.refresh");
            directory.Text=Ui.T("models.storage",Path.Combine(Api.Root,"models"));
            if(!ready && models.Count==0) message.Text=Ui.T("models.loading");
            var ids=new HashSet<string>(models.Select(row=>Api.Value(row,"id")));
            foreach(string id in controls.Keys.Where(id=>!ids.Contains(id)).ToArray()) {
                rows.Children.Remove(controls[id].View); controls.Remove(id);
            }
            foreach(var data in models) {
                string id=Api.Value(data,"id");
                ModelRow control;
                if(!controls.TryGetValue(id,out control)) {
                    control=new ModelRow(this,id);
                    controls.Add(id,control);rows.Children.Add(control.View);
                }
                control.Update(data);
            }
        }
        async Task Command(string id,string action) {
            var model=models.FirstOrDefault(row=>Api.Value(row,"id")==id);
            if(model==null || closed || token==null) return;
            if(action=="source") {
                Uri link;
                if(Uri.TryCreate(Api.Value(model,"source_url"),UriKind.Absolute,out link) && link.Scheme==Uri.UriSchemeHttps)
                    Process.Start(new ProcessStartInfo(link.AbsoluteUri){UseShellExecute=true});
                return;
            }
            if(action=="delete" && MessageBox.Show(window,Ui.T("models.promptDelete",Api.Value(model,"name")),Ui.T("models.heading"),
                MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
            object payload=new {};
            if(action=="import") {
                var dialog=new OpenFileDialog { Title=Ui.T("models.import"),Filter="GGUF (*.gguf)|*.gguf",CheckFileExists=true };
                if(dialog.ShowDialog(window)!=true) return;
                payload=new {path=dialog.FileName};
            }
            await Api.Call("/api/model-manager/"+Uri.EscapeDataString(id)+"/"+action,payload,timeout:15000,token:lifetime.Token,managementToken:token);
            await Refresh();
        }
        sealed class ModelRow {
            internal readonly Border View;
            readonly TextBlock name=new TextBlock { FontWeight=FontWeights.SemiBold }, detail=new TextBlock {TextWrapping=TextWrapping.Wrap};
            readonly TextBlock licensing=new TextBlock { FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,0) };
            readonly ProgressBar progress=new ProgressBar { Height=4,Margin=new Thickness(0,5,0,2),Maximum=1 };
            readonly Dictionary<string,Button> buttons=new Dictionary<string,Button>();

            internal ModelRow(ModelManagerView manager,string id) {
                var stack=new StackPanel { Margin=new Thickness(0,9,0,9) };
                stack.Children.Add(name);stack.Children.Add(detail);stack.Children.Add(licensing);stack.Children.Add(progress);
                var actions=new WrapPanel {Margin=new Thickness(0,4,0,0)};
                foreach(string a in new[]{"install","import","cancel","delete","source"}) {
                    string operation=a;
                    var button=new Button { Padding=new Thickness(8,2,8,2),MinHeight=25,FontSize=12,Margin=new Thickness(0,2,6,0) };
                    button.SetValue(AutomationProperties.AutomationIdProperty,id+"."+operation);
                    button.Click+=async(s,e)=>{
                        button.IsEnabled=false;
                        try { await manager.Command(id,operation); }
                        catch(Exception error) { if(!manager.closed) manager.message.Text=Ui.T("models.error",App.Message(error)); }
                        finally { button.IsEnabled=true; }
                    };
                    buttons[a]=button; actions.Children.Add(button);
                }
                stack.Children.Add(actions);
                View=new Border { BorderBrush=(Brush)manager.window.FindResource("Line"),BorderThickness=new Thickness(0,0,0,1),Child=stack };
            }
            internal void Update(Dictionary<string,object> row) {
                string state=Api.Value(row,"status","missing");
                string status=Ui.T("models.status."+state);
                long total=Bytes(row,"total_bytes"), received=Bytes(row,"received_bytes");
                bool busy=state=="downloading" || state=="importing" || state=="verifying";
                name.Text=Api.Value(row,"name");
                var info=new List<string>{status};
                if(total>0) info.Add(busy ? Size(received)+" / "+Size(total) : Size(total));
                string extra=Api.Value(row,"error");
                if(extra.Length>0) info.Add(Ui.Diagnostic(extra).ToString());
                else if(!Flag(row,"available") && (state=="installed" || Api.Value(row,"storage")=="ollama"))
                    info.Add(Ui.Diagnostic(Api.Value(row,"reason")).ToString());
                detail.Text=String.Join("  ·  ",info.Where(item=>item.Length>0));
                string license=Api.Value(row,"license");
                licensing.Text=license.Length>0 ? Ui.T("models.license")+" · "+license : "";
                if(Api.Value(row,"storage").IndexOf("ollama",StringComparison.OrdinalIgnoreCase)>=0)
                    licensing.Text+=(licensing.Text.Length>0 ? "  ·  " : "")+Ui.T("models.shared");
                licensing.Visibility=licensing.Text.Length>0 ? Visibility.Visible : Visibility.Collapsed;
                progress.Visibility=busy ? Visibility.Visible : Visibility.Collapsed;
                progress.IsIndeterminate=busy && (total<=0 || state=="verifying");
                progress.Value=total>0 ? Math.Min(1,Math.Max(0,(double)received/total)) : 0;
                foreach(var entry in buttons) {
                    string action=entry.Key;
                    entry.Value.Content=Ui.T("models."+(action=="install" ? "download" : action));
                    bool enabled=action=="source" ? Api.Value(row,"source_url").Length>0 : Flag(row,"can_"+action);
                    entry.Value.Visibility=enabled ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }
    }
}
