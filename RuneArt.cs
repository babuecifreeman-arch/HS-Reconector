using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace HearthstoneReconnector
{
    internal static class RuneArt
    {
        internal static GraphicsPath Rounded(RectangleF box, float radius) {
            float d = radius * 2; var path = new GraphicsPath();
            path.AddArc(box.Left, box.Top, d, d, 180, 90);
            path.AddArc(box.Right-d, box.Top, d, d, 270, 90);
            path.AddArc(box.Right-d, box.Bottom-d, d, d, 0, 90);
            path.AddArc(box.Left, box.Bottom-d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        static readonly Bitmap Artwork = LoadArtwork("CatBase");
        static readonly Bitmap Paws = LoadArtwork("CatPaws");
        // Both layers retain the original canvas coordinates; never crop them independently.
        static readonly Rectangle Source = FindBounds(Artwork);
        static Bitmap LoadArtwork(string name) {
            using(var stream=typeof(RuneArt).Assembly.GetManifestResourceStream(name)) {
                if(stream==null)throw new InvalidOperationException("Embedded " + name + " missing.");
                using(var image=Image.FromStream(stream))return new Bitmap(image);
            }
        }
        static Rectangle FindBounds(Bitmap image) {
            int left=image.Width,top=image.Height,right=0,bottom=0;
            for(int y=0;y<image.Height;y+=4)for(int x=0;x<image.Width;x+=4)if(image.GetPixel(x,y).A>16){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
            return Rectangle.FromLTRB(Math.Max(0,left-4),Math.Max(0,top-4),Math.Min(image.Width,right+5),Math.Min(image.Height,bottom+5));
        }
        static readonly Dictionary<string,Bitmap[]> Layers=new Dictionary<string,Bitmap[]>();
        static Bitmap[] GetLayers(int width,int height) {
            string key=width+"x"+height;Bitmap[] layers;
            if(Layers.TryGetValue(key,out layers))return layers;
            layers=new Bitmap[2];
            for(int i=0;i<2;i++) {
                layers[i]=new Bitmap(width,height,PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(layers[i])) {
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    var input=i==0?Artwork:Paws;
                    g.DrawImage(input,new Rectangle(0,0,width,height),Source.X*input.Width/(float)Artwork.Width,Source.Y*input.Height/(float)Artwork.Height,Source.Width*input.Width/(float)Artwork.Width,Source.Height*input.Height/(float)Artwork.Height,GraphicsUnit.Pixel);
                }
            }
            Layers.Add(key,layers);return layers;
        }
        internal static Bitmap Render(int size,float hover,bool pressed,bool disabled){return Render(size,size,hover,pressed,disabled);}
        internal static Bitmap Render(int width,int height,float hover,bool pressed,bool disabled) { return Render(width,height,hover,pressed,disabled,false,0,Color.FromArgb(235,77,45),false); }
        internal static Bitmap Render(int width,int height,float hover,bool pressed,bool disabled,bool busy,float phase,Color color,bool flashing) {
            var layers=GetLayers(width,height);
            var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(bitmap))using(var attributes=new ImageAttributes()) {
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.SmoothingMode=SmoothingMode.AntiAlias;
                float brightness=disabled?.55f:pressed?.8f:1f+hover*.18f;
                var matrix=new ColorMatrix();matrix.Matrix00=matrix.Matrix11=matrix.Matrix22=brightness;attributes.SetColorMatrix(matrix);
                if(pressed){g.TranslateTransform(3,3);g.ScaleTransform((width-6f)/width,(height-6f)/height);}
                g.DrawImage(layers[0],new Rectangle(0,0,width,height),0,0,width,height,GraphicsUnit.Pixel,attributes);
                // Artwork-space coordinates make paws and claw tips move together.
                g.ScaleTransform(width/(float)Source.Width,height/(float)Source.Height);g.TranslateTransform(-Source.X,-Source.Y);
                for(int side=0;side<2;side++) {
                    float wave=(float)Math.Sin(phase*Math.PI/180+side*Math.PI);
                    float drop=busy?(wave+1)*40:0;
                    float pulse=(busy||flashing)?.2f+.8f*(wave+1)/2:.65f;
                    var state=g.Save();
                    // Prevent moving arms from painting over the bottom gold frame.
                    g.SetClip(new RectangleF(side==0?190:1190,190,side==0?425:440,Artwork.Height*.724f-190));
                    g.TranslateTransform(0,drop);
                    float x=side==0?275:1270;
                    for(int claw=0;claw<4;claw++) {
                        float endX=x+claw*83;
                        float tipX=endX+(side==0?-40:40);
                        float tipY=claw==1?155:claw==2?205:270;
                        float endY=claw==0||claw==3?480:440;
                        using(var path=new GraphicsPath()) {
                            path.AddPolygon(new PointF[]{new PointF(tipX,tipY),new PointF(tipX+8,tipY+74),new PointF((tipX+endX)/2+15,(tipY+endY)/2),new PointF(endX,endY),new PointF((tipX+endX)/2-12,(tipY+endY)/2),new PointF(tipX-5,tipY+88)});
                            using(var glow=new Pen(Color.FromArgb((int)(50*pulse*Math.Min(1,brightness)),color),24))g.DrawPath(glow,path);
                            using(var brush=new SolidBrush(Color.FromArgb((int)(235*pulse*Math.Min(1,brightness)),color)))g.FillPath(brush,path);
                            using(var core=new Pen(Color.FromArgb((int)(200*pulse*Math.Min(1,brightness)),255,237,195),4))g.DrawLine(core,tipX,tipY+45,endX,endY-15);
                        }
                    }
                    g.DrawImage(layers[1],Source,0,0,width,height,GraphicsUnit.Pixel,attributes);
                    g.Restore(state);
                }
            }
            return bitmap;
        }
    }
    internal sealed class RuneButton : Control
    {
        internal event EventHandler FrameChanged;
        internal bool ExternalInput;
        internal int CaptionSpace;
        bool InButton(Point point){return new Rectangle(0,CaptionSpace,Width,Height-CaptionSpace).Contains(point);}
        void Changed() { Invalidate(); if(FrameChanged!=null)FrameChanged(this,EventArgs.Empty); }
        internal void InputEnter(){OnMouseEnter(EventArgs.Empty);}
        internal void InputLeave(){OnMouseLeave(EventArgs.Empty);}
        internal void InputDown(MouseEventArgs e){OnMouseDown(e);}
        internal void InputUp(MouseEventArgs e){OnMouseUp(e);}
        internal void PreviewPhase(float value){phase=value;Changed();}
        readonly Timer animation = new Timer();
        bool hover, pressed, busy;
        bool actionEnabled = true;
        bool moveMode;
        float blend, phase;
        DateTime flashUntil;
        bool error;
        bool warning;
        internal bool Busy { get { return busy; } set { busy = value; Changed(); } }
        internal bool MoveMode { get { return moveMode; } set { moveMode = value; pressed = false; Cursor = value ? Cursors.SizeAll : Cursors.Hand; Changed(); } }
        internal bool ActionEnabled { get { return actionEnabled; } set { actionEnabled = value; if(!value) pressed = false; Changed(); } }
        internal void Flash(bool failed) { error = failed; warning = false; flashUntil = DateTime.UtcNow.AddSeconds(3); Changed(); }
        internal void FlashWarning() { error = false; warning = true; flashUntil = DateTime.UtcNow.AddSeconds(5); Changed(); }
        internal void PreviewState(bool hovered, bool pushed) { hover = hovered; blend = hovered ? 1 : 0; pressed = pushed; Changed(); }
        public RuneButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand; TabStop = false; AccessibleName = "Переподключиться"; AccessibleRole = AccessibleRole.PushButton;
            animation.Interval = 30;
            animation.Tick += delegate {
                float target = hover && Enabled && actionEnabled ? 1f : 0f;
                bool ended = flashUntil != DateTime.MinValue && DateTime.UtcNow >= flashUntil;
                if(ended) { flashUntil = DateTime.MinValue; Changed(); }
                if(Math.Abs(blend - target) > .01 || busy || DateTime.UtcNow < flashUntil) { blend += (target - blend) * .25f; phase = (phase + 7) % 360; Changed(); }
            };
            animation.Start();
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Changed(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Changed(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if(e.Button == MouseButtons.Left && Enabled && actionEnabled && !moveMode && InButton(e.Location)) { pressed = true; if(!ExternalInput) Capture = true; Changed(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) {
            bool activate = pressed && e.Button == MouseButtons.Left && Enabled && actionEnabled && !moveMode && InButton(e.Location);
            pressed = false; if(!ExternalInput) Capture = false; Changed(); base.OnMouseUp(e); if(activate) OnClick(EventArgs.Empty);
        }
        protected override void OnEnabledChanged(EventArgs e) { pressed = false; Changed(); base.OnEnabledChanged(e); }
        internal Bitmap RenderFrame() {
            Color color=busy?Color.FromArgb(111,225,255):error?Color.FromArgb(242,79,64):warning?Color.FromArgb(244,188,71):Color.FromArgb(123,230,161);
            bool flashing=DateTime.UtcNow<flashUntil;
            int bodyHeight=Height-CaptionSpace;
            var frame=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(frame)) {
                g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var art=RuneArt.Render(Width,bodyHeight,blend,pressed,(!Enabled||!actionEnabled)&&!busy&&!moveMode,busy,phase,(busy||flashing)?color:Color.FromArgb(235,77,45),flashing))g.DrawImageUnscaled(art,0,CaptionSpace);
                if(busy && CaptionSpace>0)DrawLoadingCaption(g);
                g.TranslateTransform(0,CaptionSpace);
            if(moveMode) {
                using(var path = RuneArt.Rounded(new RectangleF(4, 4, Width - 8, bodyHeight - 8), 9))
                using(var pen = new Pen(Color.FromArgb(255, 212, 100), 2.5f)) { pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, path); }
                float x = Width - 18, y = 17;
                using(var pen = new Pen(Color.FromArgb(255, 224, 146), 1.8f)) {
                    g.DrawLine(pen,x-6,y,x+6,y); g.DrawLine(pen,x,y-6,x,y+6);
                    g.DrawLines(pen,new PointF[] { new PointF(x-3,y-3),new PointF(x,y-6),new PointF(x+3,y-3) });
                    g.DrawLines(pen,new PointF[] { new PointF(x-3,y+3),new PointF(x,y+6),new PointF(x+3,y+3) });
                    g.DrawLines(pen,new PointF[] { new PointF(x-3,y-3),new PointF(x-6,y),new PointF(x-3,y+3) });
                    g.DrawLines(pen,new PointF[] { new PointF(x+3,y-3),new PointF(x+6,y),new PointF(x+3,y+3) });
                }
            }
            }
            return frame;
        }
        void DrawLoadingCaption(Graphics g) {
            using(var path=new GraphicsPath())using(var format=new StringFormat()) {
                format.Alignment=StringAlignment.Center;format.LineAlignment=StringAlignment.Center;
                using(var font=new Font("Georgia",15,FontStyle.Bold,GraphicsUnit.Pixel))
                    path.AddString("Спасибо Боб",font.FontFamily,(int)FontStyle.Bold,15,new RectangleF(2,0,Width-4,CaptionSpace-2),format);
                using(var outline=new Pen(Color.FromArgb(230,25,13,5),3)){outline.LineJoin=LineJoin.Round;g.DrawPath(outline,path);}
                using(var gradient=new LinearGradientBrush(new RectangleF(0,0,Width,CaptionSpace),Color.Gold,Color.Gold,0f)) {
                    gradient.InterpolationColors=new ColorBlend{Colors=new Color[]{Color.FromArgb(255,169,48),Color.FromArgb(255,242,168),Color.FromArgb(115,227,255),Color.FromArgb(255,242,168),Color.FromArgb(255,169,48)},Positions=new float[]{0,.25f,.5f,.75f,1}};
                    gradient.WrapMode=WrapMode.Tile;gradient.TranslateTransform(phase/360f*Width*2,0);
                    g.FillPath(gradient,path);
                }
            }
        }
        protected override void OnPaint(PaintEventArgs e) { using(var frame=RenderFrame())e.Graphics.DrawImageUnscaled(frame,0,0); }
        protected override void Dispose(bool disposing) { if(disposing) animation.Dispose(); base.Dispose(disposing); }
    }
}

