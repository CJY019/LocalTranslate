using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace LocalTranslate {
    internal static class SelectionOcr {
        // Qt/DWM resampling can move the last pixel of a flat span several RGB levels
        // toward its background. The enclosing shell and gesture endpoints verify its edge.
        const int FillTolerance=10;
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint which);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        internal static Task<string> TryReadAsync(IntPtr root, Native.PointI start, Native.PointI end, Func<bool> isCurrent,
                                                int? verifiedBackground=null,int? verifiedHighlight=null) {
            return Task.Run(async () => {
                try {
                    if (root==IntPtr.Zero || !isCurrent() || Native.GetForegroundWindow()!=root ||
                        Native.RootAt(start)!=root || Native.RootAt(end)!=root) return null;
                    if (SelectionDiagnostics.Source(root)!="wechat") return null;
                    double scale=Math.Max(1,Math.Min(4,GetDpiForWindow(root)/96.0));
                    bool click=Math.Abs(end.X-start.X)<=3 && Math.Abs(end.Y-start.Y)<=3;
                    int dy=Math.Abs(start.Y-end.Y);
                    if (dy>360*scale || Math.Abs(start.X-end.X)>1800*scale) return Reject("gesture.bounds");
                    Native.RectI bounds;
                    if (!Native.GetWindowRect(root,out bounds)) return null;
                    int padX=(int)Math.Ceiling((click ? 135 : dy>8*scale ? 600 : 32)*scale), padY=(int)Math.Ceiling(40*scale);
                    var client=System.Windows.Forms.Screen.FromPoint(new Point(start.X,start.Y)).Bounds;
                    int inset=(int)Math.Ceiling(9*scale);
                    client.Intersect(Rectangle.FromLTRB(bounds.Left+inset,bounds.Top+inset,bounds.Right-inset,bounds.Bottom-inset));
                    var area=Rectangle.Intersect(client,
                        Rectangle.FromLTRB(Math.Min(start.X,end.X)-padX,Math.Min(start.Y,end.Y)-padY,
                        Math.Max(start.X,end.X)+padX+1,Math.Max(start.Y,end.Y)+padY+1));
                    if (!area.Contains(start.X,start.Y) || !area.Contains(end.X,end.Y) || area.Width<8 || area.Height<10)
                        return Reject("gesture.bounds");
                    if (!Unobscured()) return Reject("obscured");
                    using (var bitmap=Capture(area)) {
                        if (!Unobscured()) return Reject("obscured");
                        var press=new Point(start.X-area.X,start.Y-area.Y);
                        var release=new Point(end.X-area.X,end.Y-area.Y);
                        var pixels=Pixels(bitmap);
                        var selection=FindHighlights(pixels,area.Width,area.Height,press,release,scale,verifiedBackground,verifiedHighlight);
                        if (selection==null) return Reject("highlight.bounds");
                        var text=await RecognizeLinesAsync(pixels,area.Width,selection,isCurrent).ConfigureAwait(false);
                        if (String.IsNullOrWhiteSpace(text)) return Reject("no.text");
                        if (!Unobscured()) return Reject("source.changed");
                        // A keyboard action or arriving message may repaint the source during OCR.
                        // Verify the ink and its enclosing edges against a fresh capture.
                        foreach (var line in selection) {
                            var verify=Rectangle.Inflate(line,1,1);
                            var screen=verify;
                            screen.Offset(area.Location);
                            using (var current=Capture(screen)) {
                                var now=Pixels(current);
                                for (int y=0;y<verify.Height;y++) for (int x=0;x<verify.Width;x++)
                                    if (Distance(now[y*verify.Width+x],pixels[(verify.Top+y)*area.Width+verify.Left+x])!=0) return Reject("pixels.changed");
                            }
                        }
                        return Unobscured() ? text : Reject("source.changed");
                    }

                    bool Unobscured() {
                        if (!isCurrent() || Native.GetForegroundWindow()!=root) return false;
                        if (Native.RootAt(new Native.PointI { X=area.Left,Y=area.Top })!=root ||
                            Native.RootAt(new Native.PointI { X=area.Right-1,Y=area.Top })!=root ||
                            Native.RootAt(new Native.PointI { X=area.Left,Y=area.Bottom-1 })!=root ||
                            Native.RootAt(new Native.PointI { X=area.Right-1,Y=area.Bottom-1 })!=root) return false;
                        // A small always-on-top window can obscure the middle without covering the corners.
                        int count=0;
                        for (var window=GetWindow(root,3);window!=IntPtr.Zero;window=GetWindow(window,3)) {
                            if (++count>1024) return false;
                            Native.RectI rect;
                            if (!IsWindowVisible(window) || IsIconic(window) || !Native.GetWindowRect(window,out rect) ||
                                !area.IntersectsWith(Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom))) continue;
                            int cloaked;
                            if (DwmGetWindowAttribute(window,14,out cloaked,4)!=0 || cloaked==0) return false;
                        }
                        return true;
                    }
                } catch (Exception err) { return Reject("exception."+err.GetType().Name); }
            });
        }

        static string Reject(string reason) {
            SelectionDiagnostics.Record("selection.ocr."+reason);
            return null;
        }

        static Bitmap Capture(Rectangle area) {
            var bitmap=new Bitmap(area.Width,area.Height,PixelFormat.Format32bppRgb);
            try {
                using (var graphics=Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(area.Location,Point.Empty,area.Size,CopyPixelOperation.SourceCopy);
                return bitmap;
            } catch { bitmap.Dispose(); throw; }
        }

        static int[] Pixels(Bitmap bitmap) {
            int width=bitmap.Width,height=bitmap.Height;
            var data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);
            var pixels=new int[width*height];
            try {
                for (int y=0;y<height;y++) Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,y*width,width);
            } finally { bitmap.UnlockBits(data); }
            return pixels;
        }

        internal static Task<string> ReadHighlightAsync(Bitmap bitmap,Point start,Point end,double scale,
                                                       int? verifiedBackground=null,int? verifiedHighlight=null) {
            var pixels=Pixels(bitmap);
            var lines=FindHighlights(pixels,bitmap.Width,bitmap.Height,start,end,scale,verifiedBackground,verifiedHighlight);
            return RecognizeLinesAsync(pixels,bitmap.Width,lines,()=>true);
        }

        static async Task<string> RecognizeLinesAsync(int[] pixels,int width,List<Rectangle> lines,Func<bool> current) {
            if (lines==null || lines.Count==0) return null;
            var results=new List<string>();
            foreach (var line in lines) {
                if (!current()) return null;
                string text=await RecognizeCropAsync(pixels,width,line).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(text)) return null;
                results.Add(text);
            }
            return String.Join(Environment.NewLine,results);
        }

        static async Task<string> RecognizeCropAsync(int[] pixels,int width,Rectangle bounds) {
            int background=pixels[bounds.Top*width+bounds.Left];
            // The OCR buffer contains only pixels inside the confirmed selection, on a white margin.
            int factor=Math.Max(1,Math.Min(4,(int)Math.Round(84.0/bounds.Height)));
            int margin=12, tile=bounds.Width*factor+margin*2, rows=bounds.Height*factor+margin*2;
            // Windows OCR may skip an isolated glyph. Repeating a short crop provides line
            // context using only selected pixels. All three readings must independently agree.
            int copies=bounds.Width<=bounds.Height*3 ? 3 : 1;
            int stride=tile*copies;
            if (stride>OcrEngine.MaxImageDimension || rows>OcrEngine.MaxImageDimension) return null;
            var gray=new byte[stride*rows];
            var inkColumns=new bool[bounds.Width];
            for (int i=0;i<gray.Length;i++) gray[i]=255;
            int darkest=1;
            for (int y=bounds.Top;y<bounds.Bottom;y++) for (int x=bounds.Left;x<bounds.Right;x++)
                darkest=Math.Max(darkest,Distance(background,pixels[y*width+x]));
            for (int y=0;y<bounds.Height;y++) for (int x=0;x<bounds.Width;x++) {
                int contrast=Distance(background,pixels[(bounds.Top+y)*width+bounds.Left+x]);
                if (contrast>=Math.Max(32,darkest/3)) inkColumns[x]=true;
                byte value=(byte)(255-contrast*255/darkest);
                for (int dy=0;dy<factor;dy++) {
                    int offset=(margin+y*factor+dy)*stride+margin+x*factor;
                    for (int k=0;k<copies;k++) for (int dx=0;dx<factor;dx++) gray[offset+k*tile+dx]=value;
                }
            }
            using (var image=SoftwareBitmap.CreateCopyFromBuffer(gray.AsBuffer(),BitmapPixelFormat.Gray8,stride,rows)) {
                string english=await Recognize("en-US").ConfigureAwait(false);
                string chinese=await Recognize("zh-Hans-CN").ConfigureAwait(false);
                int han=0;
                if (chinese!=null) foreach (char c in chinese) if (c>='\u3400' && c<='\u9fff') han++;
                if (han>=2 || String.IsNullOrWhiteSpace(english) ||
                    (han==1 && (english.Length<=2 || chinese.Length>1))) return chinese ?? english;
                return english;

                async Task<string> Recognize(string language) {
                    var engine=OcrEngine.TryCreateFromLanguage(new Language(language));
                    if (engine==null) return null;
                    var result=await engine.RecognizeAsync(image).AsTask().ConfigureAwait(false);
                    if (result.Lines.Count!=1 || String.IsNullOrWhiteSpace(result.Text)) return null;
                    var words=result.Lines[0].Words;
                    string agreed=null;
                    for (int k=0;k<copies;k++) {
                        var line=new StringBuilder();
                        var covered=new bool[bounds.Width];
                        OcrWord previous=null;
                        foreach (var word in words) {
                            var box=word.BoundingRect;
                            if (box.Right<=k*tile || box.Left>=(k+1)*tile) continue;
                            if (box.Left<k*tile || box.Right>(k+1)*tile) return null;
                            if (previous!=null) {
                                // WinRT inserts spaces between Han tokens regardless of the source.
                                bool cjk=HasCjk(previous.Text) || HasCjk(word.Text);
                                double gap=box.Left-previous.BoundingRect.Right;
                                if (!cjk || gap>Math.Max(previous.BoundingRect.Height,box.Height)*0.26) line.Append(' ');
                            }
                            line.Append(word.Text); previous=word;
                            int left=(int)Math.Floor((box.Left-k*tile-margin)/factor)-2;
                            int right=(int)Math.Ceiling((box.Right-k*tile-margin)/factor)+2;
                            for (int x=Math.Max(0,left);x<Math.Min(bounds.Width,right);x++) covered[x]=true;
                        }
                        for (int x=0;x<bounds.Width;x++) if (inkColumns[x] && !covered[x]) return null;
                        string text=line.ToString().Trim();
                        if (text.Length==0 || (agreed!=null && agreed!=text)) return null;
                        agreed=text;
                    }
                    return agreed;
                }
            }
        }
        static bool HasCjk(string text) {
            foreach (char c in text) if ((c>='\u3000' && c<='\u9fff') || (c>='\uff01' && c<='\uffef')) return true;
            return false;
        }

        static int Distance(int a,int b) {
            return Math.Max(Math.Abs((a&255)-(b&255)),Math.Max(Math.Abs(((a>>8)&255)-((b>>8)&255)),
                Math.Abs(((a>>16)&255)-((b>>16)&255))));
        }

        static bool Mint(int color) {
            int r=(color>>16)&255, g=(color>>8)&255, b=color&255;
            return g-b>=16 && g-r>=18 && b-r>=10;
        }

        static List<Rectangle> FindHighlights(int[] pixels,int width,int height,Point start,Point end,double scale,int? background,int? highlight) {
            var single=FindSingle(pixels,width,height,start,end,scale,background,highlight);
            if (!single.IsEmpty) return new List<Rectangle> {single};
            if (Math.Abs(start.Y-end.Y)<8*scale) return null;
            return FindMultiple(pixels,width,height,start.Y<end.Y ? start : end,start.Y<end.Y ? end : start,scale,background,highlight);
        }

        static List<Rectangle> FindMultiple(int[] pixels,int width,int height,Point first,Point last,double scale,int? background,int? highlight) {
            // The top and bottom padding of each painted line expose its full, flat span.
            // OCR sees only a line whose entire rectangular span can be reconstructed.
            int color;
            if (background.HasValue && highlight.HasValue) color=highlight.Value;
            else {
                var votes=new Dictionary<int,int>();
                int highest=0; color=0;
                int radius=(int)Math.Ceiling(12*scale);
                for (int y=Math.Max(0,first.Y-radius);y<Math.Min(height,first.Y+radius+1);y++)
                    for (int x=Math.Max(0,first.X-radius);x<Math.Min(width,first.X+radius+1);x++) {
                        int c=pixels[y*width+x]&0xffffff;
                        if (!Mint(c)) continue;
                        int count; votes.TryGetValue(c,out count); votes[c]=++count;
                        if (count>highest) { highest=count; color=c; }
                    }
                if (highest<8*scale) return null;
            }
            int from=Math.Max(0,first.Y-(int)(44*scale)), to=Math.Min(height,last.Y+(int)(44*scale)+1);
            var spans=new List<Rectangle>();
            bool blankSinceBand=true;
            for (int y=from;y<to;y++) {
                int left=width,right=0,matched=0;
                for (int x=0;x<width;x++) if (Distance(pixels[y*width+x],color)<=FillTolerance) {
                    if (left==width) left=x;
                    right=x+1; matched++;
                }
                if (matched==0) { blankSinceBand=true; continue; }
                if (left==0 || right==width) return null;
                if (matched<4 || matched!=right-left) continue;
                int n=spans.Count;
                if (n>0 && !blankSinceBand && spans[n-1].Left==left && spans[n-1].Right==right)
                    spans[n-1]=Rectangle.FromLTRB(left,spans[n-1].Top,right,y+1);
                else spans.Add(Rectangle.FromLTRB(left,y,right,y+1));
                blankSinceBand=false;
            }
            if (spans.Count<2 || spans.Count>12) return null;
            var opening=spans[0]; var closing=spans[spans.Count-1];
            if (opening.Top<1 || closing.Bottom>=height || first.Y<opening.Top || first.Y>=opening.Bottom ||
                last.Y<closing.Top || last.Y>=closing.Bottom || Math.Abs(first.X-opening.Left)>20*scale ||
                Math.Abs(last.X-closing.Right)>20*scale) return null;
            int shell=background ?? pixels[(opening.Top-1)*width+opening.Left+opening.Width/2];
            if (Distance(shell,color)<16 || !Margin(opening,opening.Top-1) || !Margin(closing,closing.Bottom)) return null;
            for (int i=0;i<spans.Count;i++) {
                var box=spans[i];
                if (box.Height<8*scale || box.Height>44*scale || box.Width<4) return null;
                if (i>0) {
                    var previous=spans[i-1];
                    if (box.Top-previous.Bottom>Math.Max(3*scale,Math.Min(previous.Height,box.Height)*0.48) ||
                        Math.Abs(previous.Height-box.Height)>Math.Max(4*scale,box.Height*0.28) ||
                        (i==1 ? opening.Left<box.Left-8*scale : Math.Abs(box.Left-spans[1].Left)>8*scale)) return null;
                    // A missing row of highlight could represent an unrecognized intervening line.
                    for (int y=previous.Bottom;y<box.Top;y++) for (int x=0;x<width;x++)
                        if (Distance(pixels[y*width+x],color)<=FillTolerance) return null;
                }
                int leftEdge=0,rightEdge=0,leftShell=0,rightShell=0,ink=0;
                for (int y=box.Top;y<box.Bottom;y++) {
                    int offset=y*width;
                    if (Distance(pixels[offset+box.Left],color)<=FillTolerance) leftEdge++;
                    if (Distance(pixels[offset+box.Right-1],color)<=FillTolerance) rightEdge++;
                    if (Distance(pixels[offset+box.Left-1],shell)<12) leftShell++;
                    if (Distance(pixels[offset+box.Right],shell)<12) rightShell++;
                    for (int x=box.Left;x<box.Right;x++) if (Distance(pixels[offset+x],color)>48) ink++;
                    // A wider partially painted row means these witnesses describe only part of a selection.
                    for (int x=0;x<width;x++) if ((x<box.Left || x>=box.Right) && Distance(pixels[offset+x],color)<=FillTolerance) return null;
                }
                if (Math.Min(leftEdge,rightEdge)<box.Height*0.57 || Math.Min(leftShell,rightShell)<box.Height*0.54 ||
                    ink<Math.Max(4,box.Width*box.Height*0.018) || ink>box.Width*box.Height*0.62) return null;
            }
            return spans;

            bool Margin(Rectangle rect,int y) {
                int n=0;
                for (int x=rect.Left;x<rect.Right;x++) if (Distance(pixels[y*width+x],shell)<12) n++;
                return n>rect.Width*0.82;
            }
        }

        static Rectangle FindSingle(int[] pixels,int width,int height,Point start,Point end,double scale,int? background,int? highlight) {
            bool click=Math.Abs(start.X-end.X)<=3 && Math.Abs(start.Y-end.Y)<=3;
            int minY=Math.Min(start.Y,end.Y),maxY=Math.Max(start.Y,end.Y);
            var found=Rectangle.Empty;
            for (int top=Math.Max(1,maxY-(int)(44*scale));top<=Math.Min(height-2,minY);top++) {
                for (int x=1;x<width-1;) {
                    int left=x,color=pixels[top*width+x++];
                    while (x<width && Distance(color,pixels[top*width+x])<=FillTolerance) x++;
                    int right=x,w=right-left;
                    if (left<2 || right>=width-1 || w<4 ||
                        (highlight.HasValue && background.HasValue ? Distance(color,highlight.Value)>8 : !Mint(color))) continue;
                    if (click) {
                        if (start.X<left-2*scale || start.X>right+2*scale) continue;
                    } else if (Math.Abs(left-Math.Min(start.X,end.X))>20*scale ||
                               Math.Abs(right-Math.Max(start.X,end.X))>20*scale) continue;
                    int shell=pixels[(top-1)*width+left+w/2];
                    if (Distance(color,shell)<16 || (background.HasValue && Distance(shell,background.Value)>12) || !Row(top-1,shell,0.83)) continue;
                    for (int bottom=Math.Max(top+(int)(8*scale),maxY+1);
                         bottom<Math.Min(height-1,top+(int)(44*scale));bottom++) {
                        if (!Row(bottom-1,color,0.96) || !Row(bottom,shell,0.83)) continue;
                        int edgeRows=0, adjacentRows=0, ink=0;
                        for (int y=top;y<bottom;y++) {
                            int row=y*width;
                            if (Distance(pixels[row+left],color)<=FillTolerance && Distance(pixels[row+right-1],color)<=FillTolerance) edgeRows++;
                            if (Distance(pixels[row+left-1],shell)<10 && Distance(pixels[row+right],shell)<10) adjacentRows++;
                            for (int j=left;j<right;j++) if (Distance(pixels[row+j],color)>48) ink++;
                        }
                        int h=bottom-top;
                        if (edgeRows<h*0.58 || adjacentRows<h*0.54 || ink<Math.Max(4,w*h*0.018) || ink>w*h*0.62) continue;
                        if (!found.IsEmpty) return Rectangle.Empty;
                        found=Rectangle.FromLTRB(left,top,right,bottom);
                    }

                    bool Row(int y,int target,double fraction) {
                        int matches=0;
                        for (int j=left;j<right;j++) if (Distance(pixels[y*width+j],target)<8) matches++;
                        return matches>=w*fraction;
                    }
                }
            }
            return found;
        }
    }
}
