using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LocalTranslate {
    internal sealed class ImageView {
        readonly Window window;
        readonly TabItem tab;
        readonly Func<Model> model;
        readonly Func<Direction> direction;
        readonly Image preview;
        readonly TextBox original,result;
        readonly TextBlock status,name,ocrInfo,formats,engine;
        readonly Button run,recognize,stop;
        readonly StreamView translator;
        readonly HashSet<Task> pendingLoads=new HashSet<Task>();
        readonly HashSet<string> extensions=new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png",".jpg",".jpeg",".bmp",".gif",".tif",".tiff" };
        string path,temporaryPath,requestId;
        CancellationTokenSource reading;
        int selectionVersion;
        bool loading,translating,closed;

        internal ImageView(Window window,Func<Model> model,Func<Direction> direction) {
            this.window=window; this.model=model; this.direction=direction;
            tab=window.Get<TabItem>("ImageTab"); preview=window.Get<Image>("ImagePreview");
            original=window.Get<TextBox>("ImageSource"); result=window.Get<TextBox>("ImageResult");
            status=window.Get<TextBlock>("ImageStatus"); name=window.Get<TextBlock>("ImageName");
            ocrInfo=window.Get<TextBlock>("OcrInfo"); formats=window.Get<TextBlock>("ImageFormats");
            engine=window.Get<TextBlock>("ImageEngine"); App.Say(ocrInfo,"image.hint");
            run=window.Get<Button>("ImageTranslate"); recognize=window.Get<Button>("ImageRecognize"); stop=window.Get<Button>("ImageStop");
            translator=new StreamView(window,"ImageStatus",busy=>{ translating=busy; Refresh(); },"Image");
            original.TextChanged+=(s,e)=>{ result.Clear(); Refresh(); };
            window.Get<Button>("ChooseImage").Click+=async(s,e)=>{
                var pick=new OpenFileDialog { Filter=Ui.T("image.filter")+"|"+String.Join(";",extensions.Select(x=>"*"+x)),Title=Ui.T("image.open_title") };
                if(pick.ShowDialog(window)==true) await Open(pick.FileName);
            };
            window.Get<Button>("PasteImage").Click+=async(s,e)=>await Paste();
            window.PreviewKeyDown+=async(s,e)=>{
                if(!tab.IsSelected || e.Key!=Key.V || Keyboard.Modifiers!=ModifierKeys.Control) return;
                try {
                    if(!Clipboard.ContainsImage() && !Clipboard.ContainsFileDropList()) return;
                } catch(System.Runtime.InteropServices.COMException) { return; }
                e.Handled=true; await Paste();
            };
            stop.Click+=(s,e)=>CancelReading();
            recognize.Click+=async(s,e)=>await Recognize(false);
            run.Click+=async(s,e)=>{
                var selected=model(); var languages=direction();
                if(!String.IsNullOrWhiteSpace(original.Text)) await translator.Run(selected,languages,original.Text);
                else await Recognize(true);
            };
            UpdateFormats(); Refresh();
        }
        internal async Task LoadCapabilities() {
            try {
                var data=await Api.Call("/api/ocr/languages",timeout:15000);
                if(closed) return;
                object capabilities;
                if(data.TryGetValue("extensions",out capabilities) && capabilities is IEnumerable && !(capabilities is string)) {
                    var entries=((IEnumerable)capabilities).OfType<string>().Where(x=>x.StartsWith(".")).ToList();
                    if(entries.Count>0) { extensions.Clear(); extensions.UnionWith(entries); UpdateFormats(); }
                }
                var installed=Api.Rows(data,"languages").Select(x=>new {Code=Api.Value(x,"tag"),Name=Api.Value(x,"name")}).ToArray();
                var names=new UiText(()=>String.Join(Ui.T("common.list_separator"),installed.Select(x=>Ui.Language(x.Code,x.Name)).Distinct()));
                App.Status(ocrInfo,Ui.Join(" ",installed.Length>0 ? Ui.M("image.ocr_installed",names) : Ui.M("image.ocr_absent"),Ui.M("image.hint")));
            } catch(Exception err) {
                if(!closed) {
                    var hint=Ui.Join(" ",Ui.M("image.ocr_error"),Ui.M("image.hint"));
                    App.Status(ocrInfo,hint);
                    Ui.Bind(ocrInfo,FrameworkElement.ToolTipProperty,Ui.Join(" ",hint,Ui.Error(err)));
                }
            }
        }
        void UpdateFormats() { formats.Text=String.Join(Ui.T("common.list_separator"),extensions.Select(x=>x.Substring(1).ToUpperInvariant())); }
        internal void RefreshLanguage() { UpdateFormats(); Refresh(); }
        internal bool Accepts(string[] paths) { return paths!=null && paths.Length==1 && extensions.Contains(Path.GetExtension(paths[0])) && File.Exists(paths[0]); }
        internal Task Open(string file) { return TrackLoad(Open(file,false)); }
        async Task TrackLoad(Task task) {
            pendingLoads.Add(task);
            try { await task; }
            finally { pendingLoads.Remove(task); }
        }
        async Task Open(string file,bool owned) {
            if(closed) return;
            tab.IsSelected=true;
            int version=++selectionVersion;
            CancelReading(); translator.Cancel();
            loading=true; Refresh(); App.Say(status,"image.reading");
            try {
                var bitmap=await Task.Run(()=>ReadPreview(file));
                if(closed || version!=selectionVersion) return;
                var previous=temporaryPath;
                path=file; temporaryPath=owned ? file : null; preview.Source=bitmap;
                original.Clear(); result.Clear(); App.Status(engine,"");
                App.Status(name,owned ? Ui.M("image.clipboard") : (UiText)Path.GetFileName(file));
                if(!owned) name.ToolTip=file;
                App.Status(status,"");
                if(previous!=null && previous!=file) await DeleteTemporary(previous);
            } catch(Exception err) {
                if(version==selectionVersion && !closed) App.Say(status,"image.read_failed",Ui.Error(err));
            } finally {
                if(owned && temporaryPath!=file) await DeleteTemporary(file);
                if(version==selectionVersion && !closed) { loading=false; Refresh(); }
            }
        }
        static BitmapImage ReadPreview(string file) {
            using(var stream=File.OpenRead(file)) {
                var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
                int width=frame.PixelWidth,height=frame.PixelHeight;
                stream.Position=0;
                var bitmap=new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.StreamSource=stream;
                if(Math.Max(width,height)>1000) {
                    if(width>=height) bitmap.DecodePixelWidth=1000; else bitmap.DecodePixelHeight=1000;
                }
                bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }
        }
        Task Paste() { return TrackLoad(PasteClipboard()); }
        async Task PasteClipboard() {
            if(closed) return;
            string file=null;
            int version=selectionVersion;
            try {
                var bitmap=Clipboard.GetImage();
                if(bitmap==null) {
                    var paths=Clipboard.GetFileDropList().Cast<string>().ToArray();
                    if(Accepts(paths)) await Open(paths[0]);
                    else App.Say(status,"image.clipboard_empty");
                    return;
                }
                bitmap=bitmap.Clone(); bitmap.Freeze();
                var dir=Path.Combine(Api.Root,"temp");
                Directory.CreateDirectory(dir);
                file=Path.Combine(dir,"clipboard-"+Guid.NewGuid().ToString("N")+".png");
                // Reserve this paste's place before encoding, so a later selection always wins.
                version=++selectionVersion;
                CancelReading(); translator.Cancel(); loading=true; Refresh();
                App.Say(status,"image.reading_clipboard");
                string destination=file;
                await Task.Run(()=>{
                    var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write)) encoder.Save(output);
                });
                if(closed || version!=selectionVersion) return;
                await Open(file,true);
            } catch(Exception err) { if(!closed && version==selectionVersion) App.Say(status,"image.paste_failed",Ui.Error(err)); }
            finally {
                if(file!=null && temporaryPath!=file) await DeleteTemporary(file);
                if(file!=null && version==selectionVersion && !closed) { loading=false; Refresh(); }
            }
        }
        static async Task DeleteTemporary(string file) {
            // Windows OCR can briefly retain its FileStream after a cancellation request.
            for(int attempt=0;attempt<7;attempt++) {
                try { File.Delete(file); return; }
                catch(IOException) { if(attempt<6) await Task.Delay(100*(attempt+1)); }
                catch(UnauthorizedAccessException) { return; }
            }
        }
        async Task Recognize(bool translateAfter) {
            if(closed || path==null || reading!=null || loading) return;
            var selected=model(); var languages=direction();
            if(languages==null) { App.Say(status,"status.wait_languages"); return; }
            var token=new CancellationTokenSource();
            requestId=Guid.NewGuid().ToString("N"); reading=token; Refresh(); App.Say(status,"image.recognizing");
            try {
                var doc=await Api.Call("/api/ocr",new { id=requestId,path,language=languages.Source },300000,token.Token);
                if(reading!=token || closed) return;
                string text=Api.Value(doc,"text").Trim();
                if(text.Length==0) { App.Say(status,"image.no_text"); return; }
                original.Text=text; result.Clear();
                string provider=Api.Value(doc,"engine");
                string label=provider=="translategemma" ? "TranslateGemma 4B" : provider=="windows" ? "Windows OCR" : "";
                App.Status(engine,label);
                if(label.Length>0) App.Say(status,"image.recognized_by",label); else App.Say(status,"image.recognized");
                reading=null; requestId=null;
                if(translateAfter) await translator.Run(selected,languages,text);
            } catch(Exception err) { if(reading==token && !closed) App.Status(status,Ui.Error(err)); }
            finally { if(reading==token) { reading=null; requestId=null; } token.Dispose(); if(!closed) Refresh(); }
        }
        void CancelReading() {
            if(reading==null) return;
            var active=reading; var id=requestId;
            reading=null; requestId=null; StreamView.SignalCancel(id); active.Cancel(); App.Say(status,"status.stopped"); Refresh();
        }
        void Refresh() {
            bool busy=reading!=null || translating;
            bool hasText=!String.IsNullOrWhiteSpace(original.Text);
            original.IsReadOnly=busy || loading;
            window.Get<FrameworkElement>("ImageHint").Visibility=path==null ? Visibility.Visible : Visibility.Collapsed;
            recognize.IsEnabled=path!=null && !busy && !loading;
            run.IsEnabled=(path!=null || hasText) && !busy && !loading;
            run.Content=Ui.T(hasText ? "image.translate" : "image.recognize_translate");
            run.Visibility=busy ? Visibility.Collapsed : Visibility.Visible;
            stop.Visibility=busy ? Visibility.Visible : Visibility.Collapsed;
        }
        internal async Task Close() {
            closed=true; ++selectionVersion; CancelReading(); translator.Cancel();
            await Task.WhenAll(pendingLoads.ToArray());
            if(temporaryPath!=null) { await DeleteTemporary(temporaryPath); temporaryPath=null; }
        }
    }
}
