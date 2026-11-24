using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalTranslate {
    // Some WeChat windows draw the chat themselves and publish no accessible text range.
    // Compare a small patch at the press point in memory. The patch is never saved or used as text.
    internal sealed class SelectionHighlight {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]
        static extern int GetClassName(IntPtr hwnd,StringBuilder value,int capacity);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        static int sampling;
        readonly Rectangle area;
        readonly Point anchor;
        readonly double scale;
        readonly int[] original;
        internal int? VerifiedBackground { get; private set; }
        internal int? VerifiedHighlight { get; private set; }

        SelectionHighlight(Rectangle area,Point anchor,double scale,int[] original) {
            this.area=area; this.anchor=anchor; this.scale=scale; this.original=original;
        }

        internal static Task<SelectionHighlight> Begin(IntPtr root,Native.PointI at,Func<bool> current) {
            var windowClass=new StringBuilder(80);
            if(GetClassName(root,windowClass,windowClass.Capacity)==0 ||
                !windowClass.ToString().StartsWith("Qt",StringComparison.Ordinal)) return null;
            if(Interlocked.CompareExchange(ref sampling,1,0)!=0) return null;
            return Task.Run(()=>{
                try {
                    if(!current()) return null;
                    int pid; Native.GetWindowThreadProcessId(root,out pid);
                    using(var app=Process.GetProcessById(pid)) {
                        if(!app.ProcessName.Equals("Weixin",StringComparison.OrdinalIgnoreCase) &&
                           !app.ProcessName.Equals("WeChat",StringComparison.OrdinalIgnoreCase)) return null;
                    }
                    uint dpi=GetDpiForWindow(root);
                    double scale=Math.Max(1,Math.Min(3,dpi/96.0));
                    int cx=(int)Math.Ceiling(40*scale), cy=(int)Math.Ceiling(22*scale);
                    Native.RectI bounds;
                    var child=Native.WindowFromPoint(at);
                    if(Native.GetAncestor(child,2)!=root || !Native.GetWindowRect(child,out bounds)) return null;
                    var area=Rectangle.Intersect(Rectangle.FromLTRB(bounds.Left,bounds.Top,bounds.Right,bounds.Bottom),
                        new Rectangle(at.X-cx,at.Y-cy,2*cx+1,2*cy+1));
                    if(area.Width<12 || area.Height<12 || !Visible(root,area) || !current()) return null;
                    return new SelectionHighlight(area,new Point(at.X-area.Left,at.Y-area.Top),scale,Pixels(area));
                } catch(Exception) { return null; }
                finally { Interlocked.Exchange(ref sampling,0); }
            });
        }

        // null means the desktop could not be sampled. The caller can then use the text cursor.
        internal bool? Changed(IntPtr root,bool wordClick) {
            try {
                if(!Visible(root,area)) return null;
                int background,highlight;
                bool match=Matches(original,Pixels(area),area.Width,area.Height,anchor,scale,wordClick,out background,out highlight);
                VerifiedBackground=match ? (int?)background : null;
                VerifiedHighlight=match ? (int?)highlight : null;
                return match;
            } catch(ExternalException) { return null; }
            catch(ArgumentException) { return null; }
        }

        static bool Visible(IntPtr root,Rectangle area) {
            return Native.GetForegroundWindow()==root &&
                Native.RootAt(new Native.PointI { X=area.Left, Y=area.Top })==root &&
                Native.RootAt(new Native.PointI { X=area.Right-1, Y=area.Top })==root &&
                Native.RootAt(new Native.PointI { X=area.Left, Y=area.Bottom-1 })==root &&
                Native.RootAt(new Native.PointI { X=area.Right-1, Y=area.Bottom-1 })==root;
        }
        static int[] Pixels(Rectangle area) {
            using(var bitmap=new Bitmap(area.Width,area.Height,PixelFormat.Format32bppRgb)) {
                using(var graphics=Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(area.Location,Point.Empty,area.Size,CopyPixelOperation.SourceCopy);
                var data=bitmap.LockBits(new Rectangle(Point.Empty,area.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);
                try {
                    var pixels=new int[area.Width*area.Height];
                    Marshal.Copy(data.Scan0,pixels,0,pixels.Length);
                    return pixels;
                } finally { bitmap.UnlockBits(data); }
            }
        }

        static int Distance(int a,int b) {
            return Math.Max(Math.Abs((a&255)-(b&255)),Math.Max(
                Math.Abs(((a>>8)&255)-((b>>8)&255)),Math.Abs(((a>>16)&255)-((b>>16)&255))));
        }
        static int Bucket(int color) {
            return ((color>>12)&0xf00)|((color>>8)&0xf0)|((color>>4)&0xf);
        }

        internal static bool Matches(int[] before,int[] after,int width,int height,Point anchor,double scale,bool wordClick,
                                     out int verifiedBackground,out int verifiedHighlight) {
            verifiedBackground=verifiedHighlight=0;
            int radius=(int)Math.Ceiling(13*scale);
            int left=Math.Max(0,anchor.X-radius), right=Math.Min(width-1,anchor.X+radius);
            int top=Math.Max(0,anchor.Y-radius), bottom=Math.Min(height-1,anchor.Y+radius);
            var votes=new Dictionary<int,int>();
            int bestCount=0,background=0,highlight=0;
            for(int y=top;y<=bottom;y++) for(int x=left;x<=right;x++) {
                int i=y*width+x, a=before[i], b=after[i];
                if(Distance(a,b)<16) continue;
                int transition=(Bucket(a)<<12)|Bucket(b);
                int count; votes.TryGetValue(transition,out count); votes[transition]=++count;
                if(count>bestCount) { bestCount=count; background=a; highlight=b; }
            }
            if(bestCount<8*scale) return false;
            // A multi-line selection may also change the rows above or below the pressed line.
            // On the pressed line a vertical edge separates new highlight from an unchanged margin.
            int strip=(int)Math.Ceiling(4*scale), minimum=(int)Math.Ceiling(5*scale);
            int reach=wordClick ? (int)Math.Ceiling(32*scale) : (int)Math.Ceiling(6*scale);
            for(int edge=Math.Max(strip,anchor.X-reach);edge<=Math.Min(width-strip,anchor.X+reach);edge++) {
                if(SelectionEdge(edge,true) || SelectionEdge(edge,false)) {
                    verifiedBackground=background; verifiedHighlight=highlight;
                    return true;
                }
            }
            return false;

            bool Recolored(int index) {
                return Distance(before[index],background)<12 && Distance(after[index],highlight)<12 && Distance(before[index],after[index])>=16;
            }
            bool SelectionEdge(int edge,bool toRight) {
                int inside=toRight ? edge : edge-strip, outside=toRight ? edge-strip : edge;
                int first=-1,last=-1;
                for(int y=0;y<=height;y++) {
                    if(y<height) {
                        int changed=0,stable=0;
                        for(int j=0;j<strip;j++) {
                            if(Recolored(y*width+inside+j)) changed++;
                            int k=y*width+outside+j;
                            if(Distance(before[k],after[k])<12) stable++;
                        }
                        if(changed*3>=strip && stable*3>=strip*2) {
                            if(first<0) first=y;
                            last=y;
                            continue;
                        }
                        if(last>=0 && y-last<=Math.Ceiling(2*scale)) continue;
                    }
                    if(first>=0 && last-first+1>=minimum && last-first+1<=35*scale &&
                        anchor.Y>=first-3*scale && anchor.Y<=last+3*scale && SameGlyphs(first,last,edge,toRight)) return true;
                    first=last=-1;
                }
                return false;
            }
            bool SameGlyphs(int first,int last,int edge,bool toRight) {
                int same=0,either=0, inkColumns=0;
                // Stop accumulating at the opposite edge of a short selected word.
                int maxLength=Math.Min((int)Math.Ceiling(26*scale),toRight ? width-edge : edge);
                for(int j=0;j<maxLength;j++) {
                    int x=toRight ? edge+j : edge-1-j, changed=0, overlap=0, any=0;
                    for(int y=first;y<=last;y++) {
                        int i=y*width+x;
                        if(Recolored(i)) changed++;
                        bool oldInk=Distance(before[i],background)>=32, newInk=Distance(after[i],highlight)>=32;
                        if(oldInk || newInk) any++;
                        if(oldInk && newInk) overlap++;
                    }
                    if(changed==0 && any==0) break;
                    if(changed==0) continue;
                    if(overlap>0) inkColumns++;
                    same+=overlap; either+=any;
                }
                return inkColumns>=2 && same>=Math.Max(3,3*scale*scale) && same*5>=either*3;
            }
        }
    }
}
