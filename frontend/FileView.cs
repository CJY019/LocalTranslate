using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace LocalTranslate {
    internal sealed class FileItem : INotifyPropertyChanged {
        public string SourcePath { get; private set; }
        internal FileItem(string path) { SourcePath=path; }
        internal string Path { get { return SourcePath; } }
        public string Name { get { return System.IO.Path.GetFileName(Path); } }
        internal string Output;
        internal string CustomStem { get; private set; }
        internal string OutputExtension {
            get {
                var extension=System.IO.Path.GetExtension(Path);
                return extension.Equals(".pdf",StringComparison.OrdinalIgnoreCase) ? ".docx" : extension;
            }
        }
        public string OutputName {
            get {
                if(StateCode=="success") return System.IO.Path.GetFileName(Output);
                return CustomStem==null ? Ui.T("file.auto_name") : CustomStem+OutputExtension;
            }
        }
        public string NameHint {
            get { return CustomStem==null && StateCode!="success" ? Ui.T("file.auto_name_tip") : OutputName; }
        }
        internal void Rename(string stem) { CustomStem=stem; Notify(); }
        internal string StateCode { get; private set; } = "pending";
        UiText detail;
        int? percent;
        public string State { get { return percent.HasValue ? Ui.T("file.state.percent",percent.Value) : Ui.T("file.state."+StateCode); } }
        public string Detail { get { return detail==null ? "" : detail.ToString(); } }
        internal void Set(string state,UiText value=null,int? percentage=null) {
            if(StateCode==state && percent==percentage && Detail==(value==null ? "" : value.ToString())) return;
            StateCode=state; detail=value; percent=percentage;
            Notify();
        }
        internal void Notify() { if(PropertyChanged!=null) PropertyChanged(this,new PropertyChangedEventArgs(String.Empty)); }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class FileView {
        static readonly HashSet<string> Extensions=new HashSet<string>(new[]{".txt",".md",".docx",".pdf",".srt",".vtt"},StringComparer.OrdinalIgnoreCase);
        readonly ObservableCollection<FileItem> items=new ObservableCollection<FileItem>();
        readonly Dictionary<string,FileItem> known=new Dictionary<string,FileItem>(StringComparer.OrdinalIgnoreCase);
        readonly Window window;
        readonly Func<Model> model;
        readonly Func<Direction> languages;
        readonly Button run,retry,cancel,clear,addFiles,addFolder;
        readonly Button rename,chooseOutputDir,resetOutputDir;
        readonly ListView list;
        readonly ComboBox mode;
        readonly TextBlock status,outputDirectory;
        readonly FrameworkElement hint;
        readonly ProgressBar progress;
        bool busy,scanning,closing,cancelling;
        string jobId,outputDir;
        Task submit;
        internal FileView(Window window,Func<Model> model,Func<Direction> languages,ImageView images) {
            this.window=window; this.model=model; this.languages=languages;
            list=window.Get<ListView>("Files");
            list.ItemsSource=items;
            list.SizeChanged+=(s,e)=>{
                var columns=((GridView)list.View).Columns;
                columns[3].Width=Math.Max(155,e.NewSize.Width-columns.Take(3).Sum(c=>c.ActualWidth)-28);
            };
            list.MouseDoubleClick+=(s,e)=>{
                var container=ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) as ListViewItem;
                var row=container==null ? null : container.Content as FileItem;
                if(row==null || row.StateCode!="success" || !File.Exists(row.Output)) return;
                try { Process.Start("explorer.exe","/select,\""+row.Output+"\""); }
                catch(Exception err) { App.Status(status,Ui.Error(err)); }
            };
            run=window.Get<Button>("RunJob"); retry=window.Get<Button>("RetryJob"); cancel=window.Get<Button>("CancelJob");
            clear=window.Get<Button>("ClearFiles"); addFiles=window.Get<Button>("AddFiles"); addFolder=window.Get<Button>("AddFolder");
            rename=window.Get<Button>("RenameFile"); chooseOutputDir=window.Get<Button>("ChooseOutputDir"); resetOutputDir=window.Get<Button>("ResetOutputDir");
            outputDirectory=window.Get<TextBlock>("OutputDirectory");
            mode=window.Get<ComboBox>("FileMode"); progress=window.Get<ProgressBar>("JobProgress");
            status=window.Get<TextBlock>("JobStatus"); hint=window.Get<FrameworkElement>("DropHint");
            list.SelectionChanged+=(s,e)=>Refresh();
            rename.Click+=(s,e)=>EditName((FileItem)list.SelectedItem);
            chooseOutputDir.Click+=(s,e)=>{
                using(var pick=new Forms.FolderBrowserDialog { Description=Ui.T("file.output_dir_title"),SelectedPath=outputDir ?? "",ShowNewFolderButton=true }) {
                    if(pick.ShowDialog(new DialogOwner(window))==Forms.DialogResult.OK) { outputDir=pick.SelectedPath; Refresh(); }
                }
            };
            resetOutputDir.Click+=(s,e)=>{ outputDir=null; Refresh(); };
            addFiles.Click+=async(s,e)=>{
                var pick=new OpenFileDialog { Multiselect=true, Filter=Ui.T("file.filter")+"|*.txt;*.md;*.docx;*.pdf;*.srt;*.vtt",Title=Ui.T("file.open_title") };
                if(pick.ShowDialog(window)==true) await Add(pick.FileNames);
            };
            addFolder.Click+=async(s,e)=>{
                using(var pick=new Forms.FolderBrowserDialog { Description=Ui.T("file.folder_title"),ShowNewFolderButton=false }) {
                    if(pick.ShowDialog(new DialogOwner(window))==Forms.DialogResult.OK) await Add(new[]{pick.SelectedPath});
                }
            };
            window.PreviewDragOver+=(s,e)=>{
                if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                e.Effects=images.Accepts(paths) || !(busy||scanning) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled=true;
            };
            window.PreviewDrop+=async(s,e)=>{
                if(!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Handled=true;
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                if(images.Accepts(paths)) { await images.Open(paths[0]); return; }
                if(busy||scanning) return;
                window.Get<TabItem>("FileTab").IsSelected=true;
                if(paths!=null) await Add(paths);
            };
            clear.Click+=(s,e)=>{ items.Clear(); known.Clear(); outputDir=null; App.Status(status,""); progress.Value=0; Refresh(); };
            run.Click+=async(s,e)=>await Start(false); retry.Click+=async(s,e)=>await Start(true);
            cancel.Click+=async(s,e)=>await Cancel();
            Refresh();
        }
        sealed class DialogOwner : Forms.IWin32Window {
            public IntPtr Handle { get; private set; }
            internal DialogOwner(Window window) { Handle=new System.Windows.Interop.WindowInteropHelper(window).Handle; }
        }
        void EditName(FileItem row) {
            if(row==null || busy || scanning || row.StateCode=="success") return;
            var panel=new StackPanel { Margin=new Thickness(18) };
            var dialog=new Window { Owner=window,Title=Ui.T("file.rename"),Width=490,SizeToContent=SizeToContent.Height,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,ResizeMode=ResizeMode.NoResize,
                FontFamily=window.FontFamily,FontSize=window.FontSize,Foreground=window.Foreground,Background=window.Background,Content=panel };
            panel.Children.Add(new TextBlock { Text=Ui.T("file.source_name",row.Name),ToolTip=row.Path,TextTrimming=TextTrimming.CharacterEllipsis });
            var label=new Label { Content=Ui.T("file.stem"),Padding=new Thickness(0),Margin=new Thickness(0,12,0,5) };
            panel.Children.Add(label);
            var field=new DockPanel();
            var extension=new TextBlock { Text=row.OutputExtension,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,0,0) };
            DockPanel.SetDock(extension,Dock.Right); field.Children.Add(extension);
            var input=new TextBox { Text=row.CustomStem ?? Path.GetFileNameWithoutExtension(row.Name),Padding=new Thickness(6,4,6,4) };
            label.Target=input; field.Children.Add(input); panel.Children.Add(field);
            var message=new TextBlock { Text=Ui.T("file.name_help"),Style=(Style)window.FindResource("Caption"),Margin=new Thickness(0,6,0,0),TextWrapping=TextWrapping.Wrap };
            panel.Children.Add(message);
            var buttons=new DockPanel { Margin=new Thickness(0,14,0,0),LastChildFill=false };
            var reset=new Button { Content=Ui.T("file.reset_name") };
            var save=new Button { Content=Ui.T("common.ok"),IsDefault=true,Style=(Style)window.FindResource("Primary") };
            var dismiss=new Button { Content=Ui.T("common.cancel"),IsCancel=true,Margin=new Thickness(0,0,8,0) };
            buttons.Children.Add(reset); DockPanel.SetDock(save,Dock.Right); buttons.Children.Add(save);
            DockPanel.SetDock(dismiss,Dock.Right); buttons.Children.Add(dismiss); panel.Children.Add(buttons);
            reset.Click+=(s,e)=>{ row.Rename(null); dialog.DialogResult=true; };
            save.Click+=(s,e)=>{
                var stem=input.Text;
                if(String.IsNullOrWhiteSpace(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || stem.EndsWith(".") || stem!=stem.Trim()) {
                    message.Text=Ui.T("file.bad_name"); input.Focus(); return;
                }
                row.Rename(stem); dialog.DialogResult=true;
            };
            dialog.ContentRendered+=(s,e)=>{ input.Focus(); input.SelectAll(); };
            dialog.ShowDialog();
        }
        async Task Add(string[] paths) {
            if(busy||scanning) return;
            scanning=true; Refresh(); App.Say(status,"file.reading");
            var found=new List<string>(); var skipped=0;
            try {
                await Task.Run(()=>{
                    var queue=new Stack<string>(paths.Reverse()); var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while(queue.Count>0) {
                        var path=queue.Pop();
                        try {
                            path=Path.GetFullPath(path);
                            if(!visited.Add(path)) continue;
                            var attr=File.GetAttributes(path);
                            if((attr & FileAttributes.Directory)!=0) {
                                if((attr & FileAttributes.ReparsePoint)!=0) { skipped++; continue; }
                                foreach(var file in Directory.GetFileSystemEntries(path).OrderByDescending(x=>x,StringComparer.OrdinalIgnoreCase)) queue.Push(file);
                            } else if(Extensions.Contains(Path.GetExtension(path)) && !Path.GetFileName(path).StartsWith("~$")) found.Add(path);
                            else skipped++;
                        } catch(IOException) { skipped++; } catch(UnauthorizedAccessException) { skipped++; }
                    }
                });
                if(closing) return;
                int added=0;
                foreach(var path in found) {
                    if(known.ContainsKey(path))continue;
                    var row=new FileItem(path);
                    known.Add(path,row); items.Add(row); added++;
                }
                if(skipped>0) App.Say(status,"file.added_skipped",added,skipped); else App.Say(status,"file.added",added);
            } catch(Exception err) { App.Status(status,Ui.Error(err)); }
            finally { scanning=false; Refresh(); }
        }
        static bool Pending(FileItem row) { return row.StateCode=="pending" || row.StateCode=="cancelled"; }
        static bool InBatch(FileItem row) { return row.StateCode=="queued" || row.StateCode=="running"; }
        internal void RefreshLanguage() { foreach(var item in items) item.Notify(); }
        void Refresh() {
            bool editing=!busy&&!scanning;
            var selected=list.SelectedItem as FileItem;
            rename.IsEnabled=editing && list.SelectedItems.Count==1 && selected!=null && selected.StateCode!="success";
            chooseOutputDir.IsEnabled=editing; resetOutputDir.IsEnabled=editing && outputDir!=null;
            App.Status(outputDirectory,outputDir==null ? Ui.M("file.default_dir") : (UiText)outputDir);
            run.IsEnabled=editing && items.Any(Pending);
            run.Visibility=busy?Visibility.Collapsed:Visibility.Visible;
            retry.Visibility=editing&&items.Any(r=>r.StateCode=="failed")?Visibility.Visible:Visibility.Collapsed;
            cancel.Visibility=busy?Visibility.Visible:Visibility.Collapsed; cancel.IsEnabled=!cancelling;
            clear.IsEnabled=editing&&items.Count!=0; addFiles.IsEnabled=addFolder.IsEnabled=mode.IsEnabled=editing;
            hint.Visibility=items.Count==0?Visibility.Visible:Visibility.Collapsed;
        }
        async Task Start(bool failedOnly) {
            if(busy||scanning) return;
            var selected=model();
            if(selected==null || !selected.Available) { App.Status(status,selected==null ? Ui.M("file.choose_model") : Ui.Diagnostic(selected.Reason)); return; }
            var rows=items.Where(r=>failedOnly ? r.StateCode=="failed" : Pending(r)).ToList();
            if(rows.Count==0) return;
            var lang=languages();
            if(lang==null) { App.Say(status,"status.wait_languages"); return; }
            var body=new { paths=rows.Select(r=>r.Path).ToArray(),model_id=selected.Id,source_lang=lang.Source,target_lang=lang.Target,
                bilingual=mode.SelectedIndex==1,output_dir=outputDir,output_names=rows.Where(r=>r.CustomStem!=null).ToDictionary(r=>r.Path,r=>r.CustomStem) };
            busy=true; cancelling=false; jobId=null; progress.Value=0; Refresh();
            foreach(var r in rows) r.Set("queued");
            try {
                App.Say(status,"file.submitting");
                var creation=Create(body); submit=creation; await creation;
                if(closing)return;
                int misses=0;
                while(!closing) {
                    Dictionary<string,object> doc;
                    try { doc=await Api.Call("/api/jobs/"+Uri.EscapeDataString(jobId)); misses=0; }
                    catch(Exception err) {
                        if(++misses>=5) throw;
                        App.Say(status,"file.reconnecting",Ui.Error(err)); await Task.Delay(800); continue;
                    }
                    if(closing) return;
                    string state=Api.Value(doc,"status");
                    string current=Api.Value(doc,"current_file");
                    int unitsDone,unitsTotal;
                    Int32.TryParse(Api.Value(doc,"current_done"),out unitsDone); Int32.TryParse(Api.Value(doc,"current_total"),out unitsTotal);
                    double fraction=unitsTotal>0 ? Math.Max(0,Math.Min(1,(double)unitsDone/unitsTotal)) : 0;
                    FileItem actual;
                    if(!String.IsNullOrEmpty(current) && known.TryGetValue(current,out actual) && InBatch(actual))
                        actual.Set("running",percentage:unitsTotal>0 ? (int?)(100*fraction) : null);
                    foreach(var entry in Api.Rows(doc,"files")) {
                        FileItem row;
                        if(!known.TryGetValue(Api.Value(entry,"path"),out row)) continue;
                        var stateFile=Api.Value(entry,"status");
                        object warnings;
                        var note=entry.TryGetValue("warnings",out warnings) && warnings is object[]
                            ? Ui.Join(" · ",((object[])warnings).Select(w=>Ui.Diagnostic(Convert.ToString(w))).ToArray()) : null;
                        if(stateFile=="success") {
                            row.Output=Api.Value(entry,"output");
                            row.Set("success",Ui.Join(" · ",Ui.M("file.saved",row.Output),note));
                        } else if(stateFile=="failed") row.Set("failed",Ui.Join(" · ",Ui.Diagnostic(Api.Value(entry,"error")),note));
                        else if(stateFile=="cancelled") row.Set("cancelled",Ui.M("file.no_output"));
                    }
                    int total,done;
                    Int32.TryParse(Api.Value(doc,"total"),out total); Int32.TryParse(Api.Value(doc,"completed"),out done);
                    progress.Value=total>0 ? Math.Min(100,100.0*(done+(state=="running" && !String.IsNullOrEmpty(current) ? fraction : 0))/total) : 0;
                    if(state=="completed" || state=="cancelled" || state=="failed") {
                        foreach(var r in rows.Where(InBatch))
                            r.Set(state=="cancelled"?"cancelled":"failed",state=="cancelled"?Ui.M("file.no_output"):
                                String.IsNullOrEmpty(Api.Value(doc,"error")) ? Ui.M("file.no_result") : Ui.Diagnostic(Api.Value(doc,"error")));
                        int errors=rows.Count(r=>r.StateCode=="failed");
                        int stopped=rows.Count(r=>r.StateCode=="cancelled");
                        var summary=Ui.M("file.summary",Ui.M(state=="cancelled"?"file.job_cancelled":"file.job_done"),selected.Name,rows.Count(r=>r.StateCode=="success"),errors);
                        App.Status(status,Ui.Join(" · ",summary,stopped>0 ? Ui.M("file.cancel_count",stopped) : null));
                        break;
                    }
                    var progressText=Ui.M("file.progress",Ui.M(cancelling?"file.cancelling":"file.state.running"),done,total);
                    App.Status(status,Ui.Join(" · ",progressText,Path.GetFileName(current)));
                    await Task.Delay(450);
                }
            } catch(Exception err) {
                foreach(var r in rows.Where(InBatch)) r.Set("failed",Ui.Error(err));
                App.Status(status,Ui.Error(err));
            } finally { if(!closing) { jobId=null; submit=null; busy=false; cancelling=false; Refresh(); } }
        }
        async Task Create(object body) {
            var data=await Api.Call("/api/jobs",body,30000);
            jobId=Api.Value(data,"job_id");
            if(String.IsNullOrEmpty(jobId)) throw new UiException("error.job_id");
        }
        async Task Cancel() {
            if(!busy || cancelling) return;
            cancelling=true; Refresh(); App.Say(status,"file.cancelling");
            try {
                if(submit!=null)await submit;
                if(jobId!=null) await Api.Call("/api/jobs/"+Uri.EscapeDataString(jobId)+"/cancel",new {},3000);
            } catch(Exception err) { cancelling=false; Refresh(); App.Status(status,Ui.Error(err)); }
        }
        internal async Task Close() { closing=true; await Cancel(); }
    }
}
