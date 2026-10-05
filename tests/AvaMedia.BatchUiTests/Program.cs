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

var output = Path.GetFullPath("artifacts/batch-ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
var engine = new MediaEngine(new());
var fixture = args.FirstOrDefault();
if (fixture is null)
{
    fixture = Path.Combine(output, "fixture.mkv");
    var generated = ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25", "-t", "1", "-c:v", "ffv1", fixture]).GetAwaiter().GetResult();
    if (generated.ExitCode != 0) throw new InvalidOperationException(generated.Error);
}
AppBuilder.Configure<BatchTestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var window = new BatchToolsWindow(engine, output, [fixture], Path.Combine(output, "rename-journal.json")) { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13 };
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
