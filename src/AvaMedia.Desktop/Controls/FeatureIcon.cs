using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaMedia.Desktop.Controls;
// Generated artwork follows our original icon family; vector illustrations remain the fallback.
public sealed class FeatureIcon : Control
{
    public static readonly StyledProperty<string> KindProperty=AvaloniaProperty.Register<FeatureIcon,string>(nameof(Kind),"video");
    public static readonly StyledProperty<string> LabelProperty=AvaloniaProperty.Register<FeatureIcon,string>(nameof(Label),"MP4");
    public string Kind{get=>GetValue(KindProperty);set=>SetValue(KindProperty,value);}
    public string Label{get=>GetValue(LabelProperty);set=>SetValue(LabelProperty,value);}
    static FeatureIcon(){AffectsRender<FeatureIcon>(KindProperty,LabelProperty);}
    public FeatureIcon()=>RenderOptions.SetBitmapInterpolationMode(this,BitmapInterpolationMode.HighQuality);
    public override void Render(DrawingContext c)
    {
        var scale=Math.Min(Bounds.Width/92,Bounds.Height/80);using var transform=c.PushTransform(Matrix.CreateScale(scale,scale)*Matrix.CreateTranslation((Bounds.Width-92*scale)/2,(Bounds.Height-80*scale)/2));
        IBrush B(string color)=>Brush.Parse(color);var dark=new Pen(B("#4A535A"),2);var blue=B("#139DD9");
        void R(double x,double y,double w,double h,string color,double radius=0)=>c.DrawRectangle(B(color),null,new Rect(x,y,w,h),radius,radius);
        void L(double x,double y,double xx,double yy,string color,double thickness=3)=>c.DrawLine(new Pen(B(color),thickness),new(x,y),new(xx,yy));
        void T(string text,double x,double y,double size,string color,bool bold=false)=>c.DrawText(new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI",FontStyle.Normal,bold?FontWeight.Bold:FontWeight.Normal),size,B(color)),new(x,y));
        if (FeatureIconAssets.Get(Kind) is { } artwork)
        {
            c.DrawImage(artwork, new Rect(6,0,80,80));
            if (Kind is "video" or "formats" or "audio" or "image" or "document")
            {
                var label = Label.ToUpperInvariant();
                var color = label switch { "MKV"=>"#545451", "GIF"=>"#37B37E", "WEBM"=>"#686A5E", _=>Kind=="audio"?"#8BB839":Kind=="image"?"#23AD88":Kind=="document"?"#D16B58":"#507CAE" };
                R(7,7,Math.Max(45,label.Length*8+10),21,color,2);
                T(label,12,8,13,"#FFFFFF",true);
            }
            return;
        }
        void Film(double x,double y,double w=44,double h=45)
        {
            R(x,y,w,h,"#26333D",2);R(x+8,y+4,w-16,h-8,"#22A3D0",1);
            for(int i=0;i<4;i++){R(x+2,y+4+i*10,4,5,"#E6F0F6");R(x+w-6,y+4+i*10,4,5,"#E6F0F6");}
            L(x+9,y+15,x+w-9,y+15,"#9CE1F5",1);L(x+9,y+29,x+w-9,y+29,"#9CE1F5",1);
        }
        void Play(double x,double y,double s=20){var g=Geometry.Parse($"M {x},{y} L {x+s},{y+s/2} L {x},{y+s} Z");c.DrawGeometry(blue,null,g);}
        if(Kind is "video" or "audio" or "image" or "document" or "formats")
        {
            R(22,4,52,68,"#CCCCCC",2);R(20,2,52,68,"#FFFFFF",2);c.DrawRectangle(null,new Pen(B("#A6AAAD"),1),new Rect(20,2,52,68),2,2);
            c.DrawGeometry(B("#EDF0F2"),new Pen(B("#A6AAAD"),1),Geometry.Parse("M 58,2 L 72,16 L 58,16 Z"));
            var color=Label.ToUpperInvariant() switch{"MKV"=>"#545451","GIF"=>"#37B37E","WEBM"=>"#686A5E",_=>Kind=="audio"?"#8BB839":Kind=="image"?"#23AD88":Kind=="document"?"#D16B58":"#507CAE"};
            R(7,7,Math.Max(45,Label.Length*8+10),21,color,1);T(Label.ToUpperInvariant(),12,8,13,"#FFFFFF",true);
            if(Kind=="audio")T("♫",29,27,38,"#6EA52B",true);
            else if(Kind=="document"){for(int i=0;i<4;i++)R(29,35+i*7,32-i*2,2,"#A7BAC6");}
            else if(Kind=="image" || Label=="GIF"){R(27,34,37,27,"#183955");c.DrawGeometry(B("#F29741"),null,Geometry.Parse("M 27,58 L 40,42 L 48,50 L 55,40 L 64,60 Z"));c.DrawEllipse(B("#FCD46D"),null,new Point(55,40),4,4);}
            else Film(31,31,29,32);
        }
        else if(Kind=="clip-list")
        {
            R(20,4,52,49,"#67B8D9",2);R(24,8,44,42,"#FFFFFF");
            for(int i=0;i<4;i++)R(29,13+i*8,34,4,new[]{"#E99B44","#D8C94B","#62AB91","#63AFCE"}[i]);
            c.DrawGeometry(B("#67B8D9"),null,Geometry.Parse("M 12,49 L 80,49 L 72,73 L 20,73 Z"));R(36,54,20,4,"#FFFFFF",1);
        }
        else if(Kind=="join") {Film(6,12);Film(25,26);T("+",54,24,32,"#FF9B28",true);T("♫",72,10,43,"#63B923",true);}
        else if(Kind=="split") {Film(3,30,32,35);c.DrawEllipse(blue,null,new Point(62,21),18,18);for(int i=0;i<4;i++){double a=i*Math.PI/2;c.DrawEllipse(Brushes.White,null,new Point(62+Math.Cos(a)*9,21+Math.Sin(a)*9),4,4);}T("♫",38,28,43,"#6CAF27",true);}
        else if(Kind=="rotate")
        {
            Film(27,19,37,42);
            c.DrawGeometry(null,new Pen(B("#68AE3A"),5),Geometry.Parse("M 18,52 A 29,29 0 1 1 75,29"));
            c.DrawGeometry(B("#68AE3A"),null,Geometry.Parse("M 62,28 L 78,40 L 85,21 Z"));
        }
        else if(Kind is "crop" or "clip")
        {
            if(Kind=="clip")Film(19,8,51,43);else{c.DrawRectangle(null,new Pen(B("#A9B3BA"),2),new Rect(15,6,61,60));for(int i=0;i<4;i++)R(i%2==0?12:73,i<2?3:63,6,6,"#72BADF");}
            L(36,48,65,11,"#B8BCC0",6);L(55,48,25,11,"#B8BCC0",6);c.DrawEllipse(null,new Pen(B("#ED6251"),4),new Point(31,55),7,10);c.DrawEllipse(null,new Pen(B("#ED6251"),4),new Point(59,55),7,10);
        }
        else if(Kind=="erase") {using var rot=c.PushTransform(Matrix.CreateRotation(0.55)*Matrix.CreateTranslation(43,-8));R(5,22,21,49,"#F36E36",3);R(5,17,21,19,"#63859C",3);R(5,61,21,10,"#FFE1B9",2);} 
        else if(Kind=="frames") {Film(3,25,29,37);for(int i=0;i<3;i++){R(45,5+i*23,37,19,"#446985");c.DrawGeometry(B(i==1?"#F7943B":"#A3D8EC"),null,Geometry.Parse($"M 46,{21+i*23} L 58,{9+i*23} L 71,{20+i*23} Z"));}L(33,41,43,26,"#62B22F",2);L(33,41,43,41,"#62B22F",2);L(33,41,43,58,"#62B22F",2);}
        else if(Kind is "player" or "record") {R(12,8,68,54,"#36A9E4",2);R(17,13,58,43,"#E3F4FF");for(int i=0;i<6;i++)R(18+i*10,9,5,3,"#FEC353");Play(38,23,22);if(Kind=="record")c.DrawEllipse(B("#F26453"),Brushes.White is {}?new Pen(Brushes.White,2):null,new Point(68,55),12,12);}
        else if(Kind=="download") {Film(10,20,49,42);L(70,17,70,50,"#5FB134",5);L(59,40,70,51,"#5FB134",5);L(81,40,70,51,"#5FB134",5);}
        else if(Kind is "disc" or "archive") {c.DrawEllipse(B("#DCE2E6"),dark,new Point(43,37),30,30);c.DrawEllipse(Brushes.White,dark,new Point(43,37),9,9);L(43,8,43,26,"#FAFAFA",8);if(Kind=="archive"){R(53,15,24,49,"#E3B757",2);for(int i=0;i<7;i++)R(61,17+i*6,6,4,"#705C30");}}
        else if(Kind=="info") {c.DrawEllipse(blue,null,new Point(46,36),28,28);T("i",39,9,45,"#FFFFFF",true);}
        else {Film(12,24);T("⚙",39,3,44,"#F1B32D",true);T("⚙",60,30,28,"#D6981C",true);}
    }
}
