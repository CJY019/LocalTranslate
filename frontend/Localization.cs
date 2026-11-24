using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;

namespace LocalTranslate {
    // Keeps a message separate from its arguments, including file paths and user text.
    internal sealed class UiText : IValueConverter {
        readonly Func<string> render;
        internal UiText(Func<string> render) { this.render=render; }
        public static implicit operator UiText(string value) { return new UiText(()=>value??""); }
        public override string ToString() { return render(); }
        public object Convert(object value,Type type,object parameter,CultureInfo culture) { return ToString(); }
        public object ConvertBack(object value,Type type,object parameter,CultureInfo culture) { return Binding.DoNothing; }
    }

    internal sealed class LanguagePack {
        internal string Locale,NativeName,File,Sha256;
        internal int Bytes;
    }

    internal sealed class UiPulse : INotifyPropertyChanged {
        public int Revision { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Refresh() { Revision++; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs("Revision")); }
    }

    internal sealed class UiException : IOException {
        internal readonly string Key;
        internal UiException(string key) : base(key) { Key=key; }
        public override string Message { get { return Ui.T(Key); } }
    }

    internal static class Ui {
        const int MaxBytes=512*1024;
        static readonly Regex Placeholder=new Regex(@"\{[0-9]\}",RegexOptions.CultureInvariant);
        static readonly Dictionary<string,string> English=ReadPack(Resource("ui.en-US.json"),"en-US",null);
        static readonly Dictionary<string,string> Chinese=ReadPack(Resource("ui.zh-CN.json"),"zh-CN",English);
        static readonly string SystemLocale=CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="zh" ? "zh-CN" : "en-US";
        static readonly UiPulse Pulse=new UiPulse();
        static readonly HttpClient Client=new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(35) };
        static readonly Dictionary<string,string> DiagnosticKeys=new Dictionary<string,string>(StringComparer.Ordinal);
        static readonly List<KeyValuePair<Regex,string>> DiagnosticPatterns=new List<KeyValuePair<Regex,string>>();
        static readonly Dictionary<string,string> ChineseLanguageCodes=new Dictionary<string,string>(StringComparer.Ordinal);
        static Dictionary<string,string> strings=SystemLocale=="zh-CN" ? Chinese : English;
        static ResourceDictionary resources;
        static CultureInfo culture=CultureInfo.GetCultureInfo(SystemLocale);
        static bool started;
        internal static readonly string Release;
        internal static readonly LanguagePack[] Packs;
        internal static string Locale { get; private set; }=SystemLocale;
        internal static UiText StartupError { get; private set; }
        internal static event Action Changed;

        static Ui() {
            // The standalone csc build does not declare a target framework configuration.
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            var catalog=Fields(Json(Resource("ui.manifest.json")));
            RequireFields(catalog,"schema","release","packs");
            if(Number(catalog["schema"])!=1 || !Regex.IsMatch(Release=Text(catalog["release"]),@"\Av[0-9]+\.[0-9]+\.[0-9]+\z"))
                throw new UiException("error.pack_invalid");
            var locales=new HashSet<string>(new[]{"en-US","zh-CN"},StringComparer.OrdinalIgnoreCase);
            RequireType(catalog["packs"],"array");
            Packs=catalog["packs"].Elements().Select(node=>{
                var fields=Fields(node);
                RequireFields(fields,"locale","nativeName","file","bytes","sha256");
                var item=new LanguagePack {Locale=Text(fields["locale"]),NativeName=Text(fields["nativeName"]),File=Text(fields["file"]),Bytes=Number(fields["bytes"]),Sha256=Text(fields["sha256"])};
                if(!Regex.IsMatch(item.Locale,@"\A[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*\z") || !locales.Add(item.Locale) ||
                    String.IsNullOrWhiteSpace(item.NativeName) || item.NativeName.Length>80 || item.File!=item.Locale+".json" ||
                    item.Bytes<1 || item.Bytes>MaxBytes || !Regex.IsMatch(item.Sha256,@"\A[a-f0-9]{64}\z"))
                    throw new UiException("error.pack_invalid");
                return item;
            }).ToArray();
            foreach(var entry in Chinese) {
                if(entry.Key.StartsWith("lang.",StringComparison.Ordinal)) ChineseLanguageCodes[entry.Value]=entry.Key.Substring(5);
                if(!entry.Key.StartsWith("backend.",StringComparison.Ordinal) && !entry.Key.StartsWith("error.",StringComparison.Ordinal)) continue;
                if(!Placeholder.IsMatch(entry.Value)) { DiagnosticKeys[entry.Value.TrimEnd('。')]=entry.Key; continue; }
                var pieces=Placeholder.Split(entry.Value).Select(Regex.Escape);
                var pattern=new Regex(@"\A"+String.Join("(.+?)",pieces)+@"\z",RegexOptions.CultureInvariant|RegexOptions.Singleline,TimeSpan.FromMilliseconds(50));
                DiagnosticPatterns.Add(new KeyValuePair<Regex,string>(pattern,entry.Key));
            }
        }
        static byte[] Resource(string name) {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using(var buffer=new MemoryStream()) { stream.CopyTo(buffer); return buffer.ToArray(); }
        }
        static XElement Json(byte[] bytes) {
            if(bytes.Length==0 || bytes.Length>MaxBytes) throw new UiException("error.pack_invalid");
            var quotas=new XmlDictionaryReaderQuotas { MaxDepth=8,MaxStringContentLength=MaxBytes,MaxArrayLength=MaxBytes,MaxBytesPerRead=8192,MaxNameTableCharCount=MaxBytes };
            try {
                // Reject invalid UTF-8. Only JSON nodes with explicitly checked primitive types are read.
                new UTF8Encoding(false,true).GetCharCount(bytes);
                using(var reader=JsonReaderWriterFactory.CreateJsonReader(bytes,quotas)) return XElement.Load(reader,LoadOptions.PreserveWhitespace);
            } catch(XmlException) { throw new UiException("error.pack_invalid"); }
            catch(DecoderFallbackException) { throw new UiException("error.pack_invalid"); }
        }
        static void RequireType(XElement node,string type) {
            if((string)node.Attribute("type")!=type || node.Attributes().Any(a=>a.Name!="type"))
                throw new UiException("error.pack_invalid");
        }
        static Dictionary<string,XElement> Fields(XElement node) {
            RequireType(node,"object");
            var fields=new Dictionary<string,XElement>(StringComparer.Ordinal);
            foreach(var child in node.Elements()) {
                string name=child.Name.LocalName;
                if(child.Name.Namespace!=XNamespace.None || fields.ContainsKey(name)) throw new UiException("error.pack_invalid");
                fields.Add(name,child);
            }
            return fields;
        }
        static void RequireFields(Dictionary<string,XElement> fields,params string[] required) {
            if(fields.Count!=required.Length || !required.All(fields.ContainsKey)) throw new UiException("error.pack_invalid");
        }
        static string Text(XElement node) { RequireType(node,"string"); if(node.HasElements) throw new UiException("error.pack_invalid"); return node.Value; }
        static int Number(XElement node) {
            RequireType(node,"number"); int value;
            if(!Int32.TryParse(node.Value,NumberStyles.None,CultureInfo.InvariantCulture,out value)) throw new UiException("error.pack_invalid");
            return value;
        }
        static string Signature(string value) { return String.Concat(Placeholder.Matches(value).Cast<Match>().Select(m=>m.Value).OrderBy(v=>v,StringComparer.Ordinal)); }
        internal static Dictionary<string,string> ReadPack(byte[] bytes,string locale,Dictionary<string,string> reference) {
            var doc=Fields(Json(bytes)); RequireFields(doc,"schema","locale","strings");
            if(Number(doc["schema"])!=1 || Text(doc["locale"])!=locale) throw new UiException("error.pack_invalid");
            var entries=Fields(doc["strings"]);
            if(entries.Count==0 || (reference!=null && entries.Count!=reference.Count)) throw new UiException("error.pack_invalid");
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var entry in entries) {
                string value=Text(entry.Value), baseline;
                if(!Regex.IsMatch(entry.Key,@"\A[A-Za-z][A-Za-z0-9_.-]{0,95}\z") || value.Length==0 || value.Length>4096 ||
                    value.Any(c=>c<32 && c!='\n' && c!='\t') || Placeholder.Replace(value,"").IndexOfAny(new[]{'{','}'})>=0 ||
                    (reference!=null && (!reference.TryGetValue(entry.Key,out baseline) || Signature(value)!=Signature(baseline))))
                    throw new UiException("error.pack_invalid");
                result.Add(entry.Key,value);
            }
            return result;
        }
        internal static void EnsureInitialized() {
            if(started && resources!=null) return;
            string selected=Options.Read().ui_language;
            string locale=Effective(selected);
            Dictionary<string,string> values;
            try { values=Local(locale); }
            catch(Exception err) { StartupError=Error(err); locale=SystemLocale; values=Local(locale); }
            started=true; Activate(locale,values);
        }
        internal static string Effective(string choice) { return String.IsNullOrEmpty(choice) || choice=="system" ? SystemLocale : choice; }
        static string CachePath(LanguagePack pack) { return Path.Combine(Api.Root,"cache","langpacks",Release,pack.File); }
        static Dictionary<string,string> Local(string locale) {
            if(locale=="en-US") return English;
            if(locale=="zh-CN") return Chinese;
            var pack=Packs.FirstOrDefault(item=>item.Locale==locale);
            if(pack==null) throw new UiException("error.pack_invalid");
            return Cached(pack);
        }
        static Dictionary<string,string> Verified(LanguagePack pack,byte[] data) {
            if(data.Length!=pack.Bytes) throw new UiException("error.pack_integrity");
            using(var sha=SHA256.Create()) {
                if(BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant()!=pack.Sha256)
                    throw new UiException("error.pack_integrity");
            }
            return ReadPack(data,pack.Locale,English);
        }
        static Dictionary<string,string> Cached(LanguagePack pack) {
            string path=CachePath(pack);
            if(!File.Exists(path)) throw new UiException("error.pack_missing");
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length!=pack.Bytes) throw new UiException("error.pack_integrity");
                using(var reader=new BinaryReader(stream)) return Verified(pack,reader.ReadBytes(pack.Bytes));
            }
        }
        internal static bool HasCache(LanguagePack pack) {
            try { Cached(pack); return true; } catch(IOException) { return false; } catch(UnauthorizedAccessException) { return false; }
        }
        internal static async Task<Dictionary<string,string>> Prepare(string locale,CancellationToken token,HttpClient client=null) {
            var pack=Packs.FirstOrDefault(p=>p.Locale==locale);
            if(pack==null) return Local(locale);
            try { return Cached(pack); } catch(IOException) { } catch(UnauthorizedAccessException) { }
            var uri=new Uri("https://raw.githubusercontent.com/CJY019/LocalTranslate/"+Release+"/langpacks/"+pack.File);
            byte[] bytes;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(35));
                try {
                    using(var response=await (client??Client).GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token)) {
                        if(!response.IsSuccessStatusCode) throw new UiException("error.pack_network");
                        var length=response.Content.Headers.ContentLength;
                        if(length.HasValue && length.Value!=pack.Bytes) throw new UiException("error.pack_integrity");
                        using(var stream=await response.Content.ReadAsStreamAsync())
                        using(timeout.Token.Register(()=>stream.Dispose()))
                        using(var buffer=new MemoryStream(pack.Bytes)) {
                            var chunk=new byte[8192];
                            while(true) {
                                int read=await stream.ReadAsync(chunk,0,chunk.Length,timeout.Token);
                                if(read==0) break;
                                if(buffer.Length+read>pack.Bytes) throw new UiException("error.pack_integrity");
                                buffer.Write(chunk,0,read);
                            }
                            bytes=buffer.ToArray();
                        }
                    }
                } catch(UiException) { throw; }
                catch(Exception err) when(err is HttpRequestException || err is IOException || err is OperationCanceledException || err is ObjectDisposedException) {
                    token.ThrowIfCancellationRequested(); throw new UiException("error.pack_network");
                }
            }
            var checkedPack=Verified(pack,bytes);
            token.ThrowIfCancellationRequested();
            string dest=CachePath(pack); Directory.CreateDirectory(Path.GetDirectoryName(dest));
            string temp=dest+"."+Guid.NewGuid().ToString("N")+".part";
            try {
                File.WriteAllBytes(temp,bytes);
                if(File.Exists(dest)) File.Replace(temp,dest,null); else File.Move(temp,dest);
            } finally { if(File.Exists(temp)) File.Delete(temp); }
            return checkedPack;
        }
        internal static void Activate(string locale,Dictionary<string,string> values) {
            var next=new ResourceDictionary();
            foreach(var item in values) next.Add(item.Key,item.Value);
            next.Add("ui.xmlLanguage",XmlLanguage.GetLanguage(locale));
            try { culture=CultureInfo.GetCultureInfo(locale); } catch(CultureNotFoundException) { culture=CultureInfo.InvariantCulture; }
            strings=values; Locale=locale; Thread.CurrentThread.CurrentUICulture=culture;
            if(Application.Current!=null) {
                var dictionaries=Application.Current.Resources.MergedDictionaries;
                int previous=resources==null ? -1 : dictionaries.IndexOf(resources);
                if(previous<0) dictionaries.Add(next); else dictionaries[previous]=next;
                resources=next;
            }
            Pulse.Refresh(); Changed?.Invoke();
        }
        internal static string T(string key,params object[] args) {
            string value;
            if(!strings.TryGetValue(key,out value)) return key;
            return args==null || args.Length==0 ? value : String.Format(culture,value,args);
        }
        internal static UiText M(string key,params object[] args) { return new UiText(()=>T(key,args)); }
        internal static UiText Join(string separator,params UiText[] parts) {
            return new UiText(()=>String.Join(separator,parts.Where(p=>p!=null).Select(p=>p.ToString()).Where(s=>!String.IsNullOrEmpty(s))));
        }
        internal static string Language(string code,string fallback="") {
            if(String.IsNullOrEmpty(code)) return fallback;
            string value;
            if(strings.TryGetValue("lang."+code,out value)) return value;
            if(Regex.IsMatch(code,@"\Azh-(TW|HK|MO|Hant)(-|\z)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)) return T("lang.zh-Hant");
            if(strings.TryGetValue("lang."+code.Split('-')[0].ToLowerInvariant(),out value)) return value;
            return String.IsNullOrEmpty(fallback) ? code : fallback;
        }
        internal static UiText Diagnostic(string raw) { return Diagnostic(raw,0); }
        static UiText Diagnostic(string raw,int depth) {
            if(String.IsNullOrEmpty(raw)) return "";
            if(depth>3) return raw;
            string key;
            if(DiagnosticKeys.TryGetValue(raw.TrimEnd('。'),out key)) return M(key);
            foreach(var entry in DiagnosticPatterns) {
                Match match;
                try { match=entry.Key.Match(raw); } catch(RegexMatchTimeoutException) { continue; }
                if(!match.Success) continue;
                var args=match.Groups.Cast<Group>().Skip(1).Select(g=>(object)g.Value).ToArray();
                if(entry.Value=="backend.detect_unsupported") {
                    string code;
                    if(ChineseLanguageCodes.TryGetValue((string)args[0],out code)) args[0]=M("lang."+code);
                }
                if(entry.Value=="backend.ocr_visual" || entry.Value=="backend.catalog_read") args[0]=Diagnostic((string)args[0],depth+1);
                if(entry.Value=="backend.catalog_entry") args[1]=Diagnostic((string)args[1],depth+1);
                return M(entry.Value,args);
            }
            return raw;
        }
        internal static UiText Error(Exception err) {
            if(err is UiException) return M(((UiException)err).Key);
            if(err is HttpRequestException) return M("error.connection");
            if(err is TaskCanceledException) return M("error.timeout");
            return Diagnostic(err.GetBaseException().Message);
        }
        internal static void Bind(FrameworkElement element,DependencyProperty property,UiText value) {
            element.SetBinding(property,new Binding("Revision"){Source=Pulse,Converter=value,Mode=BindingMode.OneWay});
        }
        internal static void Present(TextBlock label,UiText message) {
            Bind(label,TextBlock.TextProperty,message??(UiText)"");
            label.SetBinding(FrameworkElement.ToolTipProperty,new Binding("Text"){Source=label,Mode=BindingMode.OneWay});
        }
    }
}
