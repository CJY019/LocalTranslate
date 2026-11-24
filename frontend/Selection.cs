using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalTranslate {
    internal static class Native {
        [StructLayout(LayoutKind.Sequential)] internal struct PointI { public int X,Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct RectI {
            public int Left,Top,Right,Bottom;
            internal bool SameBounds(RectI other) {
                return Left==other.Left && Top==other.Top && Right==other.Right && Bottom==other.Bottom;
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public int Size,Flags; public IntPtr Cursor; public PointI Position; }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorData { public int Size; public RectI Monitor,Work; public int Flags; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public PointI Point; public uint Data, Flags, Time; public UIntPtr Extra; }
        internal delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id, MouseProc callback, IntPtr module, uint thread);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(PointI pt);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr handle, uint which);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd,out RectI rect);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd,out RectI rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll",EntryPoint="LoadCursorW")] static extern IntPtr LoadCursor(IntPtr instance,IntPtr name);
        [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd,ref PointI point);
        internal delegate void ForegroundProc(IntPtr hook,uint evt,IntPtr hwnd,int obj,int child,uint thread,uint time);
        [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,ForegroundProc handler,uint pid,uint thread,uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(PointI pt, uint flags);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorData info);
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor,int kind,out uint x,out uint y);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] internal static extern int GetWindowLong(IntPtr hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW")] internal static extern int SetWindowLong(IntPtr hwnd,int index,int value);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,int width,int height,uint flags);
        internal static IntPtr RootAt(PointI p) { return GetAncestor(WindowFromPoint(p),2); }
        internal static bool HasTextCursor() {
            const int TextSelectCursor=32513; // IDC_IBEAM
            const int CursorShowing=1;
            var info=new CursorInfo { Size=Marshal.SizeOf(typeof(CursorInfo)) };
            return GetCursorInfo(ref info) && (info.Flags & CursorShowing)!=0 && info.Cursor!=IntPtr.Zero &&
                info.Cursor==LoadCursor(IntPtr.Zero,new IntPtr(TextSelectCursor));
        }
        internal static bool VisibleWindowContains(IntPtr hwnd,PointI p) {
            RectI bounds;
            return IsWindowVisible(hwnd) && GetWindowRect(hwnd,out bounds) &&
                p.X>=bounds.Left && p.X<bounds.Right && p.Y>=bounds.Top && p.Y<bounds.Bottom;
        }
        internal static void Place(Window window, PointI p, bool passive) {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (passive) SetWindowLong(hwnd, -20, GetWindowLong(hwnd,-20) | 0x08000000 | 0x80);
            var monitor = MonitorFromPoint(p,2);
            var info = new MonitorData { Size=Marshal.SizeOf(typeof(MonitorData)) };
            if (!GetMonitorInfo(monitor,ref info)) return;
            uint dpiX=96,dpiY=96;
            try { if (GetDpiForMonitor(monitor,0,out dpiX,out dpiY)!=0) dpiX=dpiY=96; } catch (DllNotFoundException) { }
            int width=(int)Math.Ceiling(window.Width*dpiX/96), height=(int)Math.Ceiling(window.Height*dpiY/96);
            int gap=(int)Math.Ceiling(10.0*dpiX/96);
            int x=Math.Max(info.Work.Left,p.X+gap+width<=info.Work.Right ? p.X+gap : p.X-gap-width);
            int y = p.Y+gap+height <= info.Work.Bottom ? p.Y+gap : Math.Max(info.Work.Top,p.Y-gap-height);
            SetWindowPos(hwnd,new IntPtr(-1),x,y,width,height,0x10);
        }
    }

    internal sealed class SelectionService : IDisposable {
        const int MouseActivate=0x21, NoActivate=3;
        const int MouseMove=0x200, LeftDown=0x201, LeftUp=0x202, LeftDoubleClick=0x203;
        const int MouseWheel=0x20A, MouseHorizontalWheel=0x20E;
        readonly Window owner;
        readonly Dispatcher dispatcher;
        readonly Action<string,Native.PointI> selected;
        readonly Native.MouseProc callback;
        readonly Native.ForegroundProc foregroundChanged;
        readonly SemaphoreSlim reader=new SemaphoreSlim(1);
        readonly HashSet<Task<string>> pendingCopies=new HashSet<Task<string>>();
        readonly int ownPid = Process.GetCurrentProcess().Id;
        internal bool LastReadByOcr { get; private set; }
        Native.PointI down, previous;
        Native.RectI sourceBounds;
        uint lastTime;
        volatile bool listening;
        bool doubleClick, haveDown, textCursorAtDown, draggedOverText;
        int dragWidth, dragHeight;
        int generation;
        Task<SelectionHighlight> pressHighlight;
        Task<SelectionHighlight> firstClickHighlight;
        IntPtr hook,foregroundHook,origin;
        Window badge;
        internal SelectionService(Window owner, Action<string,Native.PointI> selected) {
            this.owner=owner; dispatcher=owner.Dispatcher; this.selected=selected; callback=Mouse;
            foregroundChanged=(h,evt,hwnd,obj,child,thread,time)=>{
                if(listening && origin!=IntPtr.Zero && hwnd!=origin) CancelGesture();
            };
        }
        internal void Start() {
            if (listening) return;
            hook=Native.SetWindowsHookEx(14,callback,Native.GetModuleHandle(null),0);
            if (hook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            foregroundHook=Native.SetWinEventHook(3,3,IntPtr.Zero,foregroundChanged,0,0,0);
            if(foregroundHook==IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook=IntPtr.Zero; throw new UiException("error.selection_hook"); }
            listening=true;
        }
        public void Dispose() {
            listening=false; haveDown=false; pressHighlight=firstClickHighlight=null; lastTime=0; origin=IntPtr.Zero; Interlocked.Increment(ref generation);
            if (hook!=IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook=IntPtr.Zero; }
            if (foregroundHook!=IntPtr.Zero) { Native.UnhookWinEvent(foregroundHook); foregroundHook=IntPtr.Zero; }
            HideBadge();
        }
        internal async Task Close() {
            Dispose();
            try { await Task.WhenAll(pendingCopies.ToArray()); }
            catch(Exception) { } // Copy failures are handled by the badge click handler.
        }
        void HideBadge() { var shown=badge; badge=null; if(shown!=null) shown.Close(); }
        void CancelGesture() {
            int version=Interlocked.Increment(ref generation);
            haveDown=false; pressHighlight=firstClickHighlight=null; lastTime=0; origin=IntPtr.Zero;
            dispatcher.BeginInvoke(new Action(()=>{ if(version==generation) HideBadge(); }));
        }
        void QueueBadgeAction(Func<Task> action) {
            // Hook callbacks and routed clicks can enter WPF without a synchronization context.
            // Start the operation on this window's dispatcher so awaits retain its STA.
            dispatcher.BeginInvoke(new Action(async ()=>{
                try { await action(); }
                catch(Exception err) { Trace.TraceWarning("Selection UI: {0}",err.GetType().Name); }
            }));
        }
        IntPtr Mouse(int code, IntPtr msg, IntPtr data) {
            // The hook only records mouse gestures. Fetch selection text off the UI thread.
            int message=(int)msg;
            if(code>=0 && listening) {
                try {
                    if(message==MouseWheel || message==MouseHorizontalWheel) {
                        if(origin!=IntPtr.Zero || badge!=null) CancelGesture();
                    } else if(message==LeftDown) {
                        var input=(Native.MouseInput)Marshal.PtrToStructure(data,typeof(Native.MouseInput));
                        var p=input.Point;
                        var root=Native.RootAt(p); int pid;
                        Native.GetWindowThreadProcessId(root,out pid);
                        var badgeWindow=badge==null ? IntPtr.Zero : new WindowInteropHelper(badge).Handle;
                        // A layered window may be skipped by the hook's hit test. Let the badge
                        // receive its own mouse message before closing it for an outside click.
                        if (badgeWindow==IntPtr.Zero || (root!=badgeWindow && !Native.VisibleWindowContains(badgeWindow,p))) {
                            int version=Interlocked.Increment(ref generation); haveDown=false; pressHighlight=null;
                            dispatcher.BeginInvoke(new Action(HideBadge));
                            if (pid!=ownPid && root!=IntPtr.Zero && InClient(root,p) && Native.GetWindowRect(root,out sourceBounds)) {
                                doubleClick=root==origin && lastTime!=0 && unchecked(input.Time-lastTime)<=Native.GetDoubleClickTime()
                                  && Math.Abs(p.X-previous.X)<=Native.GetSystemMetrics(36) && Math.Abs(p.Y-previous.Y)<=Native.GetSystemMetrics(37);
                                previous=p; down=p; origin=root; lastTime=input.Time; haveDown=true;
                                textCursorAtDown=Native.HasTextCursor(); draggedOverText=false;
                                dragWidth=Math.Max(4,Native.GetSystemMetrics(68));
                                dragHeight=Math.Max(4,Native.GetSystemMetrics(69));
                                pressHighlight=doubleClick ? firstClickHighlight :
                                    SelectionHighlight.Begin(root,p,()=>listening && version==Volatile.Read(ref generation));
                            } else { origin=IntPtr.Zero; lastTime=0; }
                            firstClickHighlight=null;
                        }
                    } else if(message==MouseMove && haveDown && (pressHighlight!=null || (textCursorAtDown && !draggedOverText))) {
                        var p=((Native.MouseInput)Marshal.PtrToStructure(data,typeof(Native.MouseInput))).Point;
                        if(IsDrag(p)) {
                            // A late baseline may already contain the new selection.
                            if(pressHighlight!=null && !pressHighlight.IsCompleted) pressHighlight=null;
                            if(textCursorAtDown && !draggedOverText && Native.HasTextCursor() && Native.RootAt(p)==origin && InClient(origin,p))
                                draggedOverText=true;
                        }
                    } else if(message==LeftUp && haveDown) {
                        haveDown=false;
                        var highlight=pressHighlight!=null && pressHighlight.IsCompleted ? pressHighlight : null;
                        pressHighlight=null;
                        var p=((Native.MouseInput)Marshal.PtrToStructure(data,typeof(Native.MouseInput))).Point;
                        int dx=Math.Abs(p.X-down.X), dy=Math.Abs(p.Y-down.Y);
                        if(dx>=3 || dy>=3) lastTime=0;
                        else if(!doubleClick && highlight!=null) {
                            firstClickHighlight=highlight;
                            _=ForgetFirstClick(highlight);
                        }
                        var bounds=sourceBounds;
                        if ((doubleClick || dx>=3 || dy>=3) && Native.RootAt(p)==origin && InClient(origin,p) && Unmoved(origin,bounds)) {
                            int version=generation; var start=down; var root=origin;
                            bool copyGesture=textCursorAtDown && (doubleClick || IsDrag(p)) &&
                                (Native.HasTextCursor() || draggedOverText);
                            bool wordClick=doubleClick;
                            QueueBadgeAction(()=>Read(version,start,p,root,bounds,copyGesture,highlight,wordClick));
                        }
                    }
                } catch (Exception) { /* Ignore inaccessible desktop windows. */ }
            }
            return Native.CallNextHookEx(hook,code,msg,data);
        }
        bool IsDrag(Native.PointI point) {
            return Math.Abs(point.X-down.X)>=dragWidth || Math.Abs(point.Y-down.Y)>=dragHeight;
        }
        async Task ForgetFirstClick(Task<SelectionHighlight> first) {
            await Task.Delay((int)Native.GetDoubleClickTime()+25).ConfigureAwait(false);
            _=Interlocked.CompareExchange(ref firstClickHighlight,null,first);
        }
        async Task Read(int version,Native.PointI start,Native.PointI end,IntPtr root,Native.RectI bounds,bool copyGesture,
                        Task<SelectionHighlight> press,bool wordClick) {
            await Task.Delay(50); // Give the target window time to process mouse-up.
            if (!Current(version,root,bounds)) return;
            await reader.WaitAsync();
            bool allowCopy=false; string text, sourceApp=null;
            bool? changed=null;
            SelectionHighlight snapshot=null;
            try {
                if(!Current(version,root,bounds)) return;
                snapshot=press==null ? null : await press;
                if(!Current(version,root,bounds)) return;
                text=await Task.Run(()=>{
                    sourceApp=SelectionDiagnostics.Source(root);
                    if(snapshot!=null) changed=snapshot.Changed(root,wordClick);
                    return FindSelection(start,end,root,changed ?? copyGesture,out allowCopy);
                });
                // Some custom chat surfaces paint the highlight on a later frame.
                if(changed==false && String.IsNullOrWhiteSpace(text) && Current(version,root,bounds)) {
                    await Task.Delay(85);
                    if(!Current(version,root,bounds)) return;
                    text=await Task.Run(()=>{
                        changed=snapshot.Changed(root,wordClick);
                        return changed==true ? FindSelection(start,end,root,true,out allowCopy) : null;
                    });
                }
            } catch(Exception) { return; } // Inaccessible UIA fields cannot establish a safe copy target.
            finally { reader.Release(); }
            if(!Current(version,root,bounds)) return;
            bool direct=!String.IsNullOrWhiteSpace(text);
            if(sourceApp!=null) SelectionDiagnostics.Record(sourceApp+(direct ? ".range" : allowCopy ?
                (changed==true ? ".highlight" : ".cursor") : ".no-selection"));
            if(allowCopy || direct) ShowBadge(text,start,end,root,bounds,version,
                changed==true ? snapshot.VerifiedBackground : null,changed==true ? snapshot.VerifiedHighlight : null);
        }
        static bool Unmoved(IntPtr root,Native.RectI bounds) {
            Native.RectI current;
            return Native.GetWindowRect(root,out current) && bounds.SameBounds(current);
        }
        bool Current(int version,IntPtr root,Native.RectI bounds) {
            return listening && version==Volatile.Read(ref generation) && Native.GetForegroundWindow()==root && Unmoved(root,bounds);
        }
        static bool SupportsCopy(IntPtr root,AutomationElement first,AutomationElement last,AutomationElement focused) {
            int pid; Native.GetWindowThreadProcessId(root,out pid);
            using(var process=Process.GetProcessById(pid)) {
                string name=process.ProcessName;
                if(SupportsCopyApp(name,false)) return true;
                return SupportsCopyApp(name,true) && ReadOnlyText(first) && ReadOnlyText(last) &&
                    FocusContains(focused,first) && FocusContains(focused,last);
            }
        }
        static bool SupportsCopyApp(string processName,bool readOnlyText) {
            string app=processName.ToLowerInvariant();
            if(app.Contains("terminal") || app.StartsWith("conemu") || app.StartsWith("mobaxterm") ||
               app.StartsWith("vnc") || app.StartsWith("teamviewer") || app.StartsWith("remote desktop")) return false;
            switch(app) {
                case "cmd": case "conhost": case "openconsole": case "powershell": case "pwsh": case "bash":
                case "wsl": case "wslhost": case "mintty": case "putty": case "wezterm-gui": case "alacritty":
                case "tabby": case "hyper": case "kitty": case "xshell": case "securecrt": case "windowsterminal":
                case "mstsc": case "msrdc": case "rustdesk": case "anydesk": case "parsec": case "sunloginclient":
                case "todesk": case "remotedesktopmanager": case "logmein": case "citrixviewer": case "wfica32":
                case "code": case "code - insiders": case "vscodium": case "codium": case "cursor": case "windsurf":
                case "devenv": case "idea64": case "rider64": case "pycharm64": case "webstorm64": case "clion64":
                case "goland64": case "phpstorm64": case "rubymine64": case "studio64": case "eclipse":
                case "sublime_text": case "nvim-qt": case "gvim": case "emacs": case "zed": case "codex": return false;
                case "weixin": case "wechat": case "qq": case "wps": case "et": case "wpp": case "chatgpt": return true;
                default: return readOnlyText;
            }
        }
        static bool InClient(IntPtr root,Native.PointI p) {
            Native.RectI r;
            return Native.ScreenToClient(root,ref p) && Native.GetClientRect(root,out r)
                && p.X>=r.Left && p.X<r.Right && p.Y>=r.Top && p.Y<r.Bottom;
        }
        static bool AllowsCopyControl(ControlType type) {
            return type==ControlType.Text || type==ControlType.Edit || type==ControlType.Document || type==ControlType.Custom
                || type==ControlType.Pane || type==ControlType.Window || type==ControlType.Group || type==ControlType.ListItem
                || type==ControlType.DataItem || type==ControlType.List || type==ControlType.Table || type==ControlType.DataGrid
                || type==ControlType.Hyperlink;
        }
        static bool IsNonTextDragControl(ControlType type) {
            return type==ControlType.ScrollBar || type==ControlType.Thumb || type==ControlType.Separator ||
                type==ControlType.TitleBar || type==ControlType.ToolBar || type==ControlType.Menu || type==ControlType.MenuBar;
        }
        static bool SafeBranch(AutomationElement node,int pid,ref bool allowCopy,bool checkDragControl=false) {
            int i=0;
            while(node!=null && i++<32) {
                var info=node.Current;
                if(info.ProcessId!=pid) return true;
                if(info.IsPassword || (checkDragControl && IsNonTextDragControl(info.ControlType))) return false;
                if(allowCopy && !AllowsCopyControl(info.ControlType)) allowCopy=false;
                if(allowCopy) {
                    var widget=(info.ClassName+" "+info.AutomationId).ToLowerInvariant();
                    if(widget.Contains("terminal") || widget.Contains("console") || widget.Contains("xterm") || widget.Contains("monaco") ||
                       widget.Contains("remote-desktop") || widget.Contains("remoteview")) allowCopy=false;
                }
                node=TreeWalker.RawViewWalker.GetParent(node);
            }
            return node==null;
        }
        static bool ReadOnlyText(AutomationElement node) {
            object pattern;
            var type=node.Current.ControlType;
            if(type!=ControlType.Text && type!=ControlType.Document && type!=ControlType.Edit) return false;
            if(node.TryGetCurrentPattern(ValuePattern.Pattern,out pattern)) return ((ValuePattern)pattern).Current.IsReadOnly;
            if(type==ControlType.Text) return true;
            if(type!=ControlType.Document || !node.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) return false;
            return Object.Equals(((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute),true);
        }
        static bool FocusContains(AutomationElement focused,AutomationElement hit) {
            int pid=hit.Current.ProcessId;
            for(int i=0;hit!=null && i<32 && hit.Current.ProcessId==pid;i++,hit=TreeWalker.RawViewWalker.GetParent(hit))
                if(Automation.Compare(focused,hit)) return true;
            return false;
        }
        static string FindSelection(Native.PointI start,Native.PointI end,IntPtr root,bool copyGesture,out bool allowCopy) {
            allowCopy=false;
            var a=new Point(start.X,start.Y); var b=new Point(end.X,end.Y);
            var first=AutomationElement.FromPoint(a);
            if(first==null) return null;
            var last=AutomationElement.FromPoint(b);
            var focused=AutomationElement.FocusedElement;
            if (focused!=null && focused.Current.IsPassword) return null;
            int pid; Native.GetWindowThreadProcessId(root,out pid);
            int firstPid=first.Current.ProcessId;
            var nodes=new List<AutomationElement> { first,last };
            bool eligible=copyGesture && focused!=null && focused.Current.ProcessId==pid && InClient(root,start) && InClient(root,end)
                && Native.RootAt(start)==root && Native.RootAt(end)==root;
            for(int i=0;i<nodes.Count;i++) {
                var node=nodes[i];
                if(node==null) { eligible=false; continue; }
                int nodePid=node.Current.ProcessId;
                if(nodePid!=pid) eligible=false;
                if(!SafeBranch(node,nodePid,ref eligible,i==0)) return null;
            }
            if(focused!=null && focused.Current.ProcessId==firstPid) {
                if(!SafeBranch(focused,firstPid,ref eligible)) return null;
            }
            var ownerNode=first;
            bool sourceHit=Native.RootAt(start)==root;
            for (int i=0;i<32 && sourceHit;i++,ownerNode=TreeWalker.RawViewWalker.GetParent(ownerNode)) {
                if(ownerNode==null || ownerNode.Current.ProcessId!=firstPid || ownerNode.Current.IsPassword) break;
                bool selectionKnown;
                var selectedText=ReadTextPattern(ownerNode,a,b,root,out selectionKnown);
                if(!String.IsNullOrWhiteSpace(selectedText)) return selectedText;
                // The editor owns this gesture even if it only reports an insertion point.
                if(selectionKnown && ownerNode.Current.ControlType==ControlType.Edit) return null;
            }
            if(eligible) {
                var middle=AutomationElement.FromPoint(new Point((a.X+b.X)/2,(a.Y+b.Y)/2));
                if(middle!=null && middle.Current.ProcessId==pid && SafeBranch(middle,pid,ref eligible,true) && eligible)
                    allowCopy=SupportsCopy(root,first,last,focused);
            }
            return null;
        }
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref Native.PointI point);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        static Matrix TextCoordinates(AutomationElement node,TextPattern pattern,Rect control,IntPtr root) {
            if(node.Current.FrameworkId!="WPF") return Matrix.Identity;
            IntPtr hwnd=root;
            int pid=node.Current.ProcessId;
            for(var ancestor=node;ancestor!=null && ancestor.Current.ProcessId==pid;ancestor=TreeWalker.RawViewWalker.GetParent(ancestor)) {
                int handle=ancestor.Current.NativeWindowHandle;
                if(handle!=0) { hwnd=new IntPtr(handle); break; }
            }
            uint dpi=GetDpiForWindow(hwnd);
            var origin=new Native.PointI();
            if(dpi==0 || dpi==96 || !ClientToScreen(hwnd,ref origin)) return Matrix.Identity;

            // Legacy WPF TextPattern adds window-relative DIPs to the HWND's physical screen origin.
            // Other providers, including current WPF, return physical pixels throughout.
            double scale=dpi/96.0;
            var toScreen=new Matrix(scale,0,0,scale,origin.X*(1-scale),origin.Y*(1-scale));
            var visible=Rect.Empty;
            foreach(var range in pattern.GetVisibleRanges()) {
                foreach(var bounds in range.GetBoundingRectangles()) {
                    if(!bounds.IsEmpty && bounds.Width>0 && bounds.Height>0) visible.Union(bounds);
                }
            }
            if(visible.IsEmpty) return Matrix.Identity;
            var scaled=Rect.Transform(visible,toScreen);
            control.Inflate(3,3);
            if(!control.Contains(scaled) || control.Contains(visible)) return Matrix.Identity;
            toScreen.Invert();
            return toScreen;
        }
        static bool MatchesEndpoint(TextPatternRange hit,TextPatternRange range,TextPatternRangeEndpoint endpoint) {
            return hit!=null && hit.CompareEndpoints(TextPatternRangeEndpoint.Start,range,endpoint)==0;
        }
        static TextPatternRange RangeAt(TextPattern pattern,Rect control,Point point) {
            if(point.X<control.Left || point.X>=control.Right || point.Y<control.Top || point.Y>=control.Bottom) return null;
            try { return pattern.RangeFromPoint(point); }
            catch(ArgumentException) { return null; }
            catch(COMException) { return null; }
        }
        static bool GestureEndpoint(TextPattern pattern,TextPatternRange range,TextPatternRangeEndpoint endpoint,
            TextPatternRange hit,Point point,Matrix coordinates,bool allowMargin) {
            if(MatchesEndpoint(hit,range,endpoint)) return true;

            // Chromium can round to the preceding character. The managed UIA client also rejects
            // mixed WPF coordinates outside the control. Check the selected endpoint's geometry.
            var character=range.Clone();
            bool atStart=endpoint==TextPatternRangeEndpoint.Start;
            var other=atStart ? TextPatternRangeEndpoint.End : TextPatternRangeEndpoint.Start;
            character.MoveEndpointByRange(other,range,endpoint);
            if(character.MoveEndpointByUnit(other,TextUnit.Character,atStart ? 1 : -1)==0) return false;
            var direction=character.GetAttributeValue(TextPattern.TextFlowDirectionsAttribute);
            bool rtl=direction is FlowDirections && ((FlowDirections)direction & FlowDirections.RightToLeft)!=0;
            bool left=atStart!=rtl;
            double xMargin=4*coordinates.M11, yMargin=4*coordinates.M22;
            var boxes=character.GetBoundingRectangles();
            foreach(var box in boxes) {
                if(box.IsEmpty || box.Width<=0 || box.Height<=0) continue;
                var edge=left ? box.Left : box.Right;
                bool sameLine=point.Y>=box.Top-yMargin && point.Y<=box.Bottom+yMargin;
                if(sameLine && Math.Abs(point.X-edge)<=Math.Max(xMargin,Math.Min(box.Width/2,9*coordinates.M11))) return true;
                if(!allowMargin || (left ? point.X>=edge : point.X<=edge)) continue;
                character.ExpandToEnclosingUnit(TextUnit.Line);
                // A blank margin can map to this endpoint only when the rest of the line ends here.
                if(character.GetBoundingRectangles().Any(rect=>rect.Bottom>box.Top && rect.Top<box.Bottom &&
                    (left ? rect.Left<edge-xMargin : rect.Right>edge+xMargin))) return false;
                if(sameLine) return true;
                if(atStart ? point.Y>=box.Top : point.Y<=box.Bottom) return false;
                return pattern.GetVisibleRanges().All(visible=>visible.GetBoundingRectangles().All(rect=>
                    atStart ? rect.Top>=box.Top-yMargin : rect.Bottom<=box.Bottom+yMargin));
            }
            return false;
        }
        static string ReadTextPattern(AutomationElement node,Point start,Point end,IntPtr root,out bool selectionKnown) {
            selectionKnown=false;
            try {
                object pattern;
                if(!node.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) return null;
                var textPattern=(TextPattern)pattern;
                var ranges=(textPattern.GetSelection() ?? Array.Empty<TextPatternRange>())
                    .Where(range=>range.CompareEndpoints(TextPatternRangeEndpoint.Start,range,TextPatternRangeEndpoint.End)!=0).ToArray();
                if(ranges.Length==0) {
                    selectionKnown=textPattern.SupportedTextSelection!=SupportedTextSelection.None;
                    return null;
                }
                var visible=ranges.Select(range=>new {
                    Range=range,
                    Bounds=range.GetBoundingRectangles().Where(rect=>!rect.IsEmpty && rect.Width>0 && rect.Height>0).ToArray()
                }).Where(item=>item.Bounds.Length>0).ToArray();
                selectionKnown=visible.Length>0;
                if(!selectionKnown) return null;
                var control=node.Current.BoundingRectangle;
                if(control.IsEmpty || !control.Contains(start)) return null;
                var coordinates=TextCoordinates(node,textPattern,control,root);
                bool doubleClick=Math.Abs(start.X-end.X)<3 && Math.Abs(start.Y-end.Y)<3;
                var press=coordinates.Transform(start);
                // A provider may reject a point outside its own bounds. Captured drags can end there.
                var endAtControl=coordinates.Transform(new Point(Math.Max(control.Left,Math.Min(end.X,control.Right-1)),
                    Math.Max(control.Top,Math.Min(end.Y,control.Bottom-1))));
                var release=coordinates.Transform(end);
                var anchor=doubleClick ? null : RangeAt(textPattern,control,press);
                var moving=doubleClick ? null : RangeAt(textPattern,control,endAtControl);
                foreach(var item in visible) {
                    bool nearPress=item.Bounds.Any(rect=>{
                        rect.Inflate(7*coordinates.M11,4*coordinates.M22);
                        return rect.Contains(press);
                    });
                    if(!nearPress) continue;
                    if(!doubleClick) {
                        var range=item.Range;
                        var first=TextPatternRangeEndpoint.Start;
                        var last=TextPatternRangeEndpoint.End;
                        if(!(GestureEndpoint(textPattern,range,first,anchor,press,coordinates,false) &&
                             GestureEndpoint(textPattern,range,last,moving,release,coordinates,true)) &&
                           !(GestureEndpoint(textPattern,range,last,anchor,press,coordinates,false) &&
                             GestureEndpoint(textPattern,range,first,moving,release,coordinates,true))) continue;
                    }
                    return String.Join(Environment.NewLine,ranges.Select(range=>range.GetText(-1).Replace("\uFFFC",String.Empty))).Trim();
                }
                return null;
            } catch(ElementNotAvailableException) { return null; }
            catch(COMException) { return null; }
            catch(InvalidOperationException) { return null; }
            catch(UnauthorizedAccessException) { return null; }
        }
        void ShowBadge(string text,Native.PointI start,Native.PointI point,IntPtr root,Native.RectI bounds,int version,
                       int? verifiedBackground,int? verifiedHighlight) {
            HideBadge();
            var button=new Button { Focusable=false,FontSize=16,Padding=new Thickness(0),MinHeight=0,Style=(Style)Application.Current.FindResource("Primary") };
            button.SetResourceReference(ContentControl.ContentProperty,"selection.badge");
            button.SetResourceReference(FrameworkElement.ToolTipProperty,"selection.action");
            badge=new Window { Width=36,Height=34,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                ShowInTaskbar=false,ShowActivated=false,Focusable=false,Topmost=true,AllowsTransparency=true,Background=Brushes.Transparent, Content=button };
            var candidate=badge;
            var expiry=Expire(candidate,9);
            bool replaceOffered=false;
            async Task Translate() {
                if(!CandidateCurrent() || !button.IsEnabled) return;
                bool replaceClipboard=replaceOffered;
                SelectionDiagnostics.Record(replaceClipboard ? "wechat.copy-replace.click" : "badge.click");
                expiry.Stop(); button.Content="…"; button.SetResourceReference(FrameworkElement.ToolTipProperty,"selection.reading"); button.IsEnabled=false;
                bool wechat=false, recognized=false, backupUnavailable=false;
                if(String.IsNullOrWhiteSpace(text)) {
                    try {
                        // A reverse drag can place the badge on top of the original start or midpoint.
                        // Hide its HWND before UIA and WindowFromPoint inspect the source again.
                        candidate.Hide();
                        bool allow=false;
                        text=await Task.Run(()=>FindSelection(start,point,root,true,out allow));
                        if(!CandidateCurrent()) return;
                        if(allow && String.IsNullOrWhiteSpace(text)) {
                            wechat=SelectionDiagnostics.Source(root)=="wechat";
                            if(wechat && !replaceClipboard) {
                                // Keep the badge hidden so that it cannot cover the highlighted text.
                                SelectionDiagnostics.Record("wechat.ocr.start");
                                try { text=await SelectionOcr.TryReadAsync(root,start,point,()=>Current(version,root,bounds),verifiedBackground,verifiedHighlight); }
                                catch(Exception err) { SelectionDiagnostics.Record("wechat.ocr.exception."+err.GetType().Name); }
                                if(!CandidateCurrent()) return;
                                recognized=!String.IsNullOrWhiteSpace(text);
                                SelectionDiagnostics.Record(recognized ? "wechat.ocr.ready" : "wechat.ocr.empty");
                            }
                            if(!recognized) {
                                candidate.Show();
                                var clipboardWindow=new WindowInteropHelper(owner).Handle;
                                SelectionDiagnostics.Record(replaceClipboard ? "wechat.clipboard.replace" : "badge.clipboard.start");
                                Func<bool> current=()=>Current(version,root,bounds);
                                var copy=replaceClipboard
                                    ? (wechat ? ClipboardSelection.TryReadReplacingAsync(root,clipboardWindow,current) : Task.FromResult<string>(null))
                                    : ClipboardSelection.TryReadAsync(root,clipboardWindow,current,()=>backupUnavailable=true);
                                pendingCopies.Add(copy);
                                try { text=await copy; }
                                finally { pendingCopies.Remove(copy); }
                            }
                        } else if(String.IsNullOrWhiteSpace(text)) SelectionDiagnostics.Record("badge.uia.ineligible");
                    } catch(Exception err) {
                        SelectionDiagnostics.Record("badge.read.exception."+err.GetType().Name);
                        text=null;
                    }
                }
                if(!CandidateCurrent()) return;
                if(!String.IsNullOrWhiteSpace(text)) {
                    HideBadge();
                    LastReadByOcr=recognized;
                    SelectionDiagnostics.Record("badge.selection.ready");
                    selected(text,point);
                } else if(wechat && backupUnavailable && !replaceClipboard) {
                    // The clipboard can hold virtual files that the app cannot restore. A second
                    // explicit click lets the user choose whether to replace it with the selection.
                    SelectionDiagnostics.Record("wechat.copy-replace.offered");
                    candidate.Content=null;
                    var panel=new StackPanel { Background=Brushes.White,Margin=new Thickness(2) };
                    var warning=new TextBlock {FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(2,1,2,5)};
                    warning.SetResourceReference(TextBlock.TextProperty,"selection.replace"); panel.Children.Add(warning);
                    button.SetResourceReference(ContentControl.ContentProperty,"selection.copy_action"); button.FontSize=13; button.Height=27; button.IsEnabled=true;
                    button.SetResourceReference(FrameworkElement.ToolTipProperty,"selection.copy_tip");
                    panel.Children.Add(button);
                    panel.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
                    candidate.Width=Math.Max(152,Math.Ceiling(panel.DesiredSize.Width)+12); candidate.Height=56; candidate.Content=panel;
                    Native.Place(candidate,point,true); candidate.Show(); Native.Place(candidate,point,true);
                    replaceOffered=true;
                    expiry=Expire(candidate,12);
                } else {
                    SelectionDiagnostics.Record("badge.selection.empty");
                    ShowFailure(point,wechat);
                }
            }
            button.Click+=(s,e)=>QueueBadgeAction(Translate);
            Native.Place(candidate,point,true);
            var source=HwndSource.FromHwnd(new WindowInteropHelper(candidate).Handle);
            source.AddHook((IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)=>{
                if(message==MouseActivate) {
                    handled=true;
                    return new IntPtr(NoActivate);
                }
                if(message==LeftDown || message==LeftDoubleClick) {
                    // WPF's Button normally waits for capture and mouse-up before Click.
                    // A passive HWND can receive this press while the text window keeps focus.
                    handled=true;
                    if(!replaceOffered || OnCopyButton(hwnd,lParam)) QueueBadgeAction(Translate);
                }
                return IntPtr.Zero;
            });
            candidate.Show();
            Native.Place(candidate,point,true);
            bool OnCopyButton(IntPtr hwnd,IntPtr packed) {
                int clientX=unchecked((short)(packed.ToInt64() & 0xffff));
                int clientY=unchecked((short)((packed.ToInt64()>>16) & 0xffff));
                var target=HwndSource.FromHwnd(hwnd).CompositionTarget;
                if(target==null || !button.IsVisible) return false;
                var position=target.TransformFromDevice.Transform(new Point(clientX,clientY));
                var local=candidate.TranslatePoint(position,button);
                return new Rect(new Point(),button.RenderSize).Contains(local);
            }
            bool CandidateCurrent() {
                if(badge==candidate && Current(version,root,bounds)) return true;
                SelectionDiagnostics.Record("badge.candidate.invalidated");
                if(badge==candidate) HideBadge();
                return false;
            }
        }
        void ShowFailure(Native.PointI point,bool wechat) {
            HideBadge();
            var note=new TextBlock {Margin=new Thickness(7,4,7,4),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
            note.SetResourceReference(TextBlock.TextProperty,wechat ? "selection.wechat_empty" : "selection.empty");
            badge=new Window {Width=280,MinHeight=32,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,
                Background=Brushes.White,FontSize=12,Content=note};
            Native.Place(badge,point,true); badge.Show(); Native.Place(badge,point,true); Expire(badge,3);
        }
        DispatcherTimer Expire(Window shown,int seconds) {
            var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(seconds) };
            timer.Tick+=(s,e)=>{ timer.Stop(); if (badge==shown) HideBadge(); };
            shown.Closed+=(s,e)=>timer.Stop(); timer.Start(); return timer;
        }
    }
}
