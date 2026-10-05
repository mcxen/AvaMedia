using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

// Headless interaction and lifecycle checks; no user queue or preferences are touched.
var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-checks");
Directory.CreateDirectory(output);
AppBuilder.Configure<App>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var results = new List<string>();
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    results.Add(description);
    Console.WriteLine("PASS " + description);
}
void Pump(int milliseconds = 0)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    if (milliseconds > 0)
    {
        using var cancellation = new CancellationTokenSource();
        DispatcherTimer.RunOnce(cancellation.Cancel, TimeSpan.FromMilliseconds(milliseconds));
        Dispatcher.UIThread.MainLoop(cancellation.Token);
    }
    Dispatcher.UIThread.RunJobs();
}
string ColorOf(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color.ToString() : "";
bool NearColor(IBrush? brush, string expected)
{
    // Brush animation color conversion can round a channel by one byte.
    if (brush is not ISolidColorBrush solid) return false;
    var target = Color.Parse(expected);
    return solid.Color.A == target.A && Math.Abs(solid.Color.R - target.R) <= 1
        && Math.Abs(solid.Color.G - target.G) <= 1 && Math.Abs(solid.Color.B - target.B) <= 1;
}
bool Settles(Func<bool> condition)
{
    var timer = Stopwatch.StartNew();
    while (!condition() && timer.ElapsedMilliseconds < 1000) Pump(10);
    return condition();
}

Motion.SetReducedMotion(true);
var tile = new Button { Content = "转换为 MP4", Classes = { "tile" }, Width = 180, Height = 80 };
var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
content.Children.Add(new TextBlock { Text = "UI / Motion", Classes = { "title" } });
content.Children.Add(new TextBlock { Text = "浅深主题与减少动效", Classes = { "caption" } });
content.Children.Add(tile);
var window = new Window { Content = content, Width = 500, Height = 280 };
window.Show();
Pump(30);
var presenter = tile.GetVisualDescendants().OfType<ContentPresenter>()
    .First(p => p.Name == "PART_ContentPresenter");
Check(!window.Classes.Contains("motion-enabled"), "Reduced motion applies to a newly opened window");
Check(tile.Transitions is null && presenter.Transitions is null, "Reduced motion removes button and presenter transitions");
Check(Math.Abs(content.Opacity - 1) < 0.001, "Reduced motion shows window content immediately");
Check(ColorOf(window.Background) == "#fffafafa", "Light canvas resource resolves in a real window");

window.CaptureRenderedFrame()?.Save(Path.Combine(output, "components-light.png"));
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Pump(30);
Check(ColorOf(window.Background) == "#ff202020", "Application theme changes an already open window");
Check(ColorOf(tile.Background) == "#ff202020", "Tile dynamic resource follows the dark theme");
window.CaptureRenderedFrame()?.Save(Path.Combine(output, "components-dark.png"));

Motion.SetReducedMotion(false);
Pump();
bool allowedBySystem = window.Classes.Contains("motion-enabled");
// Exercise custom animations even when the host OS disables them. This changes only
// this isolated headless window's style class, never the OS or production policy.
if (!allowedBySystem) window.Classes.Add("motion-enabled");
Pump();
{
    Check(tile.Transitions?.Count == 2 && presenter.Transitions?.Count == 2,
        "Tile transform and actual template brush transitions are attached");
    var originalBounds = tile.Bounds;
    var pointer = tile.TranslatePoint(new Point(30, 30), window)!.Value;
    window.MouseMove(pointer);
    Check(Settles(() => NearColor(presenter.Background, "#193B52")), "Hover changes the actual button template surface");
    tile.IsEnabled = false;
    Check(Settles(() => NearColor(presenter.Background, "#202020")), "Disabled tile does not retain a hover surface");
    tile.IsEnabled = true;
    Settles(() => NearColor(presenter.Background, "#193B52"));
    window.MouseDown(pointer, MouseButton.Left);
    Check(Settles(() => Math.Abs(tile.RenderTransform!.Value.M11 - 0.985) < 0.001), "Press animates the tile transform");
    Check(tile.Bounds == originalBounds, "Press feedback does not reflow the layout");
    Motion.SetReducedMotion(true);
    Pump();
    Check(Math.Abs(tile.RenderTransform!.Value.M11 - 1) < 0.001, "Reducing motion during press restores transform immediately");
    window.MouseUp(pointer, MouseButton.Left);
    window.MouseMove(new Point(490, 270));
    Motion.SetReducedMotion(false);
    if (!allowedBySystem) window.Classes.Add("motion-enabled");
    Pump();
    Motion.Reveal(content);
    Pump(25);
    Check(content.Opacity >= 0.82 && content.Opacity < 1, "Reveal changes displayed opacity during the animation");
    Motion.Reveal(content);
    Motion.Reveal(content);
    Pump(25);
    Motion.SetReducedMotion(true);
    Pump(30);
    Check(Math.Abs(content.Opacity - 1) < 0.001, "Reducing motion cancels rapid reveals and restores base opacity");
    Check(tile.Transitions is null && presenter.Transitions is null, "Runtime preference removes all custom transitions");

    Motion.SetReducedMotion(false);
    if (!allowedBySystem) window.Classes.Add("motion-enabled");
    Motion.Reveal(tile);
    Pump(20);
    content.Children.Remove(tile);
    Pump(30);
    Check(Math.Abs(tile.Opacity - 1) < 0.001, "Detaching animated content cancels it and restores opacity");
    content.Children.Add(tile);
    Motion.Reveal(content);
    Pump(20);
    content.IsVisible = false;
    Pump(30);
    Check(Math.Abs(content.Opacity - 1) < 0.001, "Hiding animated content cancels it and restores opacity");
    content.IsVisible = true;
    Motion.Reveal(content);
    Pump(20);
    window.Hide();
    Pump(30);
    Check(Math.Abs(content.Opacity - 1) < 0.001, "Hiding a window cancels its content animation");
    window.Show();
    window.Classes.Add("motion-enabled");
    Motion.Reveal(content);
    Pump(20);
}

var beforeFocus = tile.Bounds;
tile.Focus(NavigationMethod.Tab);
Pump();
Check(tile.IsFocused && NearColor(presenter.BorderBrush, "#60CDFF"), "Keyboard focus displays the accent border immediately");
Check(tile.Bounds == beforeFocus, "Keyboard focus does not change layout bounds");
window.Close();
Pump(30);
Check(!window.Classes.Contains("motion-enabled"), "Closing a window releases its motion registration");
Check(Math.Abs(content.Opacity - 1) < 0.001, "Closing during reveal restores content opacity");

Motion.SetReducedMotion(true);
var settings = new AppSettings { ReduceMotion = true };
var settingsWindow = new SettingsWindow(settings);
settingsWindow.Show();
settingsWindow.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
Pump(30);
var checkBox = settingsWindow.GetVisualDescendants().OfType<CheckBox>()
    .Single(c => c.Content is string text && text.StartsWith("减少界面动效"));
Check(checkBox.IsChecked == true && checkBox.Bounds.Height > 0, "Settings exposes the current reduced-motion preference");
Check(checkBox.TranslatePoint(new Point(0, checkBox.Bounds.Height), settingsWindow)?.Y < settingsWindow.Bounds.Height,
    "Reduced-motion setting fits within its window");
settingsWindow.CaptureRenderedFrame()?.Save(Path.Combine(output, "settings-dark.png"));
settingsWindow.Close();
Pump();

// MainWindow construction and category navigation only read user state. Hide it
// before process exit so its normal Closing/save handler is never invoked.
var userState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia");
string? ReadUserFile(string name) => File.Exists(Path.Combine(userState, name)) ? File.ReadAllText(Path.Combine(userState, name)) : null;
var priorSettings = ReadUserFile("settings.json");
var priorQueue = ReadUserFile("queue.json");
var themeStorage=new Storage(Path.Combine(output,"main-settings"));themeStorage.SaveSettings(new(){Theme="Dark"});
var main = new MainWindow(themeStorage);
main.Show();
Pump(30);
main.CaptureRenderedFrame()?.Save(Path.Combine(output, "main-dark.png"));
var mainMenu = main.GetVisualDescendants().OfType<Menu>().First();
Check(ColorOf(mainMenu.Background) == "#ff282828", "Main window surfaces follow the dark application theme");
var grid = main.FindControl<Grid>("FeatureGrid")!;
var categories = main.FindControl<StackPanel>("Categories")!;
// Enable only the isolated test window's animations to exercise quick navigation.
main.Classes.Add("motion-enabled");
foreach (var category in new[] { "音频", "图片", "文档", "视频" })
{
    var button = categories.Children.OfType<Button>().Single(b =>
        b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == category));
    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump(10);
}
Pump(200);
Check(main.FindControl<TextBlock>("CategoryTitle")!.Text == "视频" && grid.Children.Count > 0,
    "Rapid category navigation leaves the latest category and content visible");
Check(Math.Abs(grid.Opacity - 1) < 0.001, "Rapid category navigation settles at full opacity");
Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
Pump(220);
main.CaptureRenderedFrame()?.Save(Path.Combine(output, "main-light.png"));
main.Hide();
Check(priorSettings == ReadUserFile("settings.json") && priorQueue == ReadUserFile("queue.json"),
    "UI verification leaves user preferences and queue unchanged");

var storage = new Storage(Path.Combine(output, "isolated-settings"));
storage.SaveSettings(settings);
Check(storage.LoadSettings().ReduceMotion, "Reduced-motion preference survives settings serialization");
File.WriteAllText(Path.Combine(output, "isolated-settings", "settings.json"), "{}");
Check(!storage.LoadSettings().ReduceMotion, "Legacy settings without the new field remain compatible");
QueueListChecks.Run(output, Check, Pump);
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { results.Count, allowedBySystem, results },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"UI checks complete: {results.Count} results. {output}");
