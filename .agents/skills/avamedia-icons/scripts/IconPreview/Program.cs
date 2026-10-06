using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

if (args.Length < 2) throw new ArgumentException("Usage: IconPreview <project-root> <output-directory>");
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"src/AvaMedia.Desktop/Assets/FeatureIcons/v2/manifest.json")));
var kinds = manifest.RootElement.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("kind").GetString()!).ToArray();
foreach (var feature in Catalog.All)
    foreach (var family in new[] { "v2", "macos9" })
        if (!kinds.Contains(feature.Icon) || !AssetLoader.Exists(new Uri($"avares://AvaMedia.Desktop/Assets/FeatureIcons/{family}/{feature.Icon}.png")))
            throw new InvalidDataException($"Missing embedded {family} icon for {feature.Id}: {feature.Icon}");
var window = new Window { Width=100, Height=60 }; window.Show(); Dispatcher.UIThread.RunJobs();
if (window.Icon is null) throw new InvalidDataException("The global window icon is not loaded.");
window.Close();
void Capture(Control control, int width, int height, string filename)
{
    control.Measure(new Size(width,height)); control.Arrange(new Rect(0,0,width,height)); Dispatcher.UIThread.RunJobs();
    using var bitmap = new RenderTargetBitmap(new PixelSize(width,height),new Vector(96,96));
    bitmap.Render(control); bitmap.Save(Path.Combine(output,filename));
}
foreach (var family in new[] { "v2", "macos9" })
foreach (var dark in new[] { false, true })
    foreach (var size in new[] { 48, 64, 120 })
    {
        Skin.Apply(family=="macos9"?"MacOS9":dark?"Dark":"Light");
        var width=size==120?1000:800; var height=size==120?800:560;
        var grid=new Grid { Width=width, Height=height, Background=Brush.Parse(dark?"#202020":family=="macos9"?"#CCCCCC":"#F7F8FA"), ColumnDefinitions=new("*,*,*,*,*"), RowDefinitions=new(string.Join(',',Enumerable.Repeat("*",(kinds.Length+4)/5))) };
        for (var i=0; i<kinds.Length; i++)
        {
            var kind=kinds[i];
            var stack=new StackPanel { Spacing=8, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
            stack.Children.Add(new FeatureIcon { Kind=kind, Label=kind=="audio"?"MP3":kind=="image"?"PNG":kind=="document"?"PDF":"MP4", Width=size*92d/80, Height=size });
            stack.Children.Add(new TextBlock { Text=kind, Foreground=dark?Brushes.White:Brushes.Black, FontSize=16, HorizontalAlignment=HorizontalAlignment.Center });
            Grid.SetColumn(stack,i%5); Grid.SetRow(stack,i/5); grid.Children.Add(stack);
        }
        var host = new Window { Content=grid, Width=width+4, Height=height+40 }; host.Show(); Dispatcher.UIThread.RunJobs();
        Capture(grid,width,height,$"{(family=="macos9"?"macos9-":"")}feature-icons-{size}px-{(dark?"dark":"light")}.png");
        host.Close();
    }
Skin.Apply("Light");
var sizes=new[] { 16,24,32,48,64,128 };
var application=new Grid { Width=960, Height=420, ColumnDefinitions=new("*,*,*,*,*,*"), RowDefinitions=new("*,*") };
var images=new List<Bitmap>();
try
{
    for (var row=0;row<2;row++)
    {
        var background=new Border { Background=Brush.Parse(row==0?"#F7F8FA":"#202020") };
        Grid.SetRow(background,row); Grid.SetColumnSpan(background,sizes.Length); application.Children.Add(background);
        for (var column=0;column<sizes.Length;column++)
        {
            var size=sizes[column];
            var bitmap=new Bitmap(Path.Combine(root,$"src/AvaMedia.Desktop/Assets/AppIcon/v2/app-{size}.png")); images.Add(bitmap);
            var image=new Image { Source=bitmap, Width=size, Height=size };
            RenderOptions.SetBitmapInterpolationMode(image,BitmapInterpolationMode.HighQuality);
            var stack=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Spacing=12 };
            stack.Children.Add(image);
            stack.Children.Add(new TextBlock { Text=$"{size} px", FontSize=14, Foreground=row==0?Brushes.Black:Brushes.White, HorizontalAlignment=HorizontalAlignment.Center });
            Grid.SetRow(stack,row); Grid.SetColumn(stack,column); application.Children.Add(stack);
        }
    }
    Capture(application,960,420,"app-icon-sizes.png");
}
finally { foreach(var bitmap in images) bitmap.Dispose(); }
File.WriteAllText(Path.Combine(output,"coverage.json"),JsonSerializer.Serialize(new { catalogEntries=Catalog.All.Count, functionalKinds=kinds.Length, macos9Kinds=kinds.Count(kind => AssetLoader.Exists(new Uri($"avares://AvaMedia.Desktop/Assets/FeatureIcons/macos9/{kind}.png"))), globalWindowIconLoaded=true, sizes },new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine($"Rendered {kinds.Length} icon kinds for {Catalog.All.Count} catalog entries; application icon loaded. {output}");
