using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LocalTranslate {
    internal static class SelectionDiagnostics {
        static readonly object Gate = new object();
        const long MaximumBytes = 128 * 1024;

        internal static string Source(IntPtr hwnd) {
            try {
                int pid; Native.GetWindowThreadProcessId(hwnd,out pid);
                using(var proc=Process.GetProcessById(pid)) {
                    string name=proc.ProcessName;
                    if(String.Equals(name,"Weixin",StringComparison.OrdinalIgnoreCase) || String.Equals(name,"WeChat",StringComparison.OrdinalIgnoreCase)) return "wechat";
                    if(String.Equals(name,"ChatGPT",StringComparison.OrdinalIgnoreCase)) return "chatgpt";
                }
            } catch(ArgumentException) { }
            catch(InvalidOperationException) { }
            catch(System.ComponentModel.Win32Exception) { }
            return null;
        }

        // Record only fixed status codes, never selected text or clipboard contents.
        internal static void Record(string status) {
            try {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                string path = Path.Combine(directory, "selection-diagnostics.log");
                lock (Gate) {
                    Directory.CreateDirectory(directory);
                    if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes) return;
                    string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    File.AppendAllText(path, time + " " + status + Environment.NewLine, new UTF8Encoding(false));
                }
            } catch (Exception) { }
        }
    }
}
