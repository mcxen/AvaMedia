using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

AppBuilder.Configure<BatchTestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var output = Path.GetFullPath("artifacts/batch-ui"); Directory.CreateDirectory(output);
var fixture = args.FirstOrDefault() ?? throw new ArgumentException("Pass a generated fixture video.");
var window = new BatchToolsWindow(new MediaEngine(new()), output, [fixture]) { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13 };
window.Show(); Dispatcher.UIThread.RunJobs();
var preview = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "预览新名称"));
preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
var entry = window.GetVisualDescendants().OfType<ListBox>().Single().Items.Cast<BatchVideoEntry>().Single();
if (string.IsNullOrWhiteSpace(entry.NewName) || !entry.NewName.Contains("_001")) throw new Exception("Rename preview did not update the list.");
Capture("rename.png");
var tab = window.GetVisualDescendants().OfType<TabControl>().Single(); tab.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Capture("contact-sheet.png");
window.Close();
Console.WriteLine("PASS: batch window layout, rename preview action, contact sheet tab. " + output);
void Capture(string name)
{
    window.Measure(new Size(1260, 860)); window.Arrange(new Rect(0, 0, 1260, 860)); Dispatcher.UIThread.RunJobs();
    using var bitmap = new RenderTargetBitmap(new PixelSize(1260, 860), new Vector(96, 96)); bitmap.Render(window); bitmap.Save(Path.Combine(output, name));
}
public sealed class BatchTestApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}
