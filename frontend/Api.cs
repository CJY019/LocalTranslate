using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalTranslate {
    internal static class Api {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        internal static bool Connected;
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = false }) {
            BaseAddress = new Uri("http://127.0.0.1:47836"), Timeout = Timeout.InfiniteTimeSpan
        };
        static Api() { ServicePointManager.DefaultConnectionLimit = 32; }
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }; }
        internal static string Value(IDictionary<string,object> row, string key, string fallback = "") {
            object value;
            return row != null && row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }
        internal static IEnumerable<Dictionary<string,object>> Rows(Dictionary<string,object> doc, string key) {
            object value;
            if (!doc.TryGetValue(key, out value) || !(value is IEnumerable)) yield break;
            foreach (var item in (IEnumerable)value) if (item is Dictionary<string,object>) yield return (Dictionary<string,object>)item;
        }
        internal static HttpRequestMessage Request(string path, object payload) {
            if(path!="/api/health" && !Connected) throw new UiException("error.offline");
            var req = new HttpRequestMessage(payload == null ? HttpMethod.Get : HttpMethod.Post, path);
            if (payload != null) req.Content = new StringContent(Json().Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }
        private static async Task Verify(HttpResponseMessage response) {
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync();
            try {
                var doc = Json().Deserialize<Dictionary<string,object>>(body);
                body = Value(doc, "message", Value(doc, "error", body));
            } catch (ArgumentException) { } catch (InvalidOperationException) { }
            throw new IOException(string.IsNullOrWhiteSpace(body) ? response.StatusCode.ToString() : body);
        }
        internal static async Task<Dictionary<string,object>> Call(string path, object payload = null, int timeout = 10000,
            CancellationToken token = default(CancellationToken), string managementToken = null) {
            using (var time = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                time.CancelAfter(timeout);
                using (var req = Request(path, payload)) {
                    if(managementToken!=null) req.Headers.Add("X-Local-Translate-Model-Token",managementToken);
                    using (var response = await Http.SendAsync(req, time.Token)) {
                        await Verify(response);
                        var body = await response.Content.ReadAsStringAsync();
                        return String.IsNullOrWhiteSpace(body) ? new Dictionary<string,object>() : Json().Deserialize<Dictionary<string,object>>(body);
                    }
                }
            }
        }
        internal static Process Launch() {
            string python = Path.Combine(Root, @"runtime\python\pythonw.exe");
            if (!File.Exists(python)) python = Environment.GetEnvironmentVariable("LOCALTRANSLATE_PYTHON");
            string server = Path.Combine(Root, @"backend\server.py");
            if (!File.Exists(python) || !File.Exists(server)) throw new UiException("error.runtime_files");
            var start = new ProcessStartInfo(python, "\"" + server + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root };
            string temp = Path.Combine(Root,"temp");
            Directory.CreateDirectory(temp);
            start.EnvironmentVariables["TEMP"] = temp;
            start.EnvironmentVariables["TMP"] = temp;
            start.EnvironmentVariables["PYTHONPYCACHEPREFIX"] = temp;
            start.EnvironmentVariables["PYTHONUTF8"] = "1";
            start.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            return Process.Start(start);
        }
        internal static async Task Translate(string id, string model, string source, Direction languages, Action<string,bool> output, CancellationToken token) {
            var body = new { id = id, model_id = model, text = source, source_lang = languages.Source, target_lang = languages.Target };
            await ReadStream("/api/translate",body,output,token);
        }
        internal static Task Chat(string id, string model, object[] messages, Action<string,bool> output, CancellationToken token) {
            return ReadStream("/api/chat",new {id,model_id=model,messages},output,token);
        }
        static async Task ReadStream(string path,object body,Action<string,bool> output,CancellationToken token) {
            using (var req = Request(path,body))
            using (var response = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)) {
                await Verify(response);
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (token.Register(() => stream.Dispose()))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null) {
                        token.ThrowIfCancellationRequested();
                        if (String.IsNullOrWhiteSpace(line)) continue;
                        var part = Json().Deserialize<Dictionary<string,object>>(line);
                        switch (Value(part,"type")) {
                            case "delta": output(Value(part,"text"), false); break;
                            case "done": output(Value(part,"text"), true); return;
                            case "error":
                                string message=Value(part,"message");
                                if(String.IsNullOrWhiteSpace(message)) throw new UiException("error.translate");
                                throw new IOException(message);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    throw new UiException("error.interrupted");
                }
            }
        }
    }

    internal class Options {
        public string ui_language { get; set; } = "system";
        public string model_id { get; set; } = "index2b";
        public string source_lang { get; set; } = "auto";
        public string target_lang { get; set; } = "zh";
        public bool selection_enabled { get; set; }
        public bool bilingual { get; set; }
        private static readonly string FileName = Path.Combine(Api.Root, "settings.json");
        internal static Options Read() {
            try {
                var raw=File.ReadAllText(FileName, Encoding.UTF8);
                var doc=Api.Json().Deserialize<Dictionary<string,object>>(raw);
                var options=Api.Json().Deserialize<Options>(raw);
                if (!doc.ContainsKey("source_lang") || !doc.ContainsKey("target_lang")) {
                    var old=Api.Value(doc,"direction");
                    if(!doc.ContainsKey("source_lang")) options.source_lang=old=="en-zh" ? "en" : old=="zh-en" ? "zh" : "auto";
                    if(!doc.ContainsKey("target_lang")) options.target_lang=old=="zh-en" ? "en" : "zh";
                }
                return options;
            }
            catch (Exception) { return new Options(); }
        }
        internal void Save() {
            var temp = FileName + ".tmp";
            File.WriteAllText(temp, Api.Json().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(FileName)) File.Replace(temp, FileName, null); else File.Move(temp, FileName);
        }
    }

    internal class Model : System.ComponentModel.INotifyPropertyChanged {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Available { get; set; }
        public string Reason { get; set; }
        internal LanguageOption[] Sources = new LanguageOption[0], Targets = new LanguageOption[0];
        public string Display { get { return Available ? Name : Ui.T("common.unavailable",Name); } }
        public override string ToString() { return Display; }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        internal void RefreshText() { PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs("Display")); }
    }
    internal class Direction {
        public string Source, Target;
    }
    internal sealed class LanguageOption : System.ComponentModel.INotifyPropertyChanged {
        public string Code { get; set; }
        string fallback;
        public string Name { get { return Ui.Language(Code,fallback); } set { fallback=value; } }
        public override string ToString() { return Name; }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        internal void RefreshText() { PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs("Name")); }
    }
}
