using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;
public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var captureRoot=desktop.Args?.Contains("--capture")==true?desktop.Args.SkipWhile(a=>a!="--capture").Skip(1).FirstOrDefault()??"artifacts":null;
            MainWindow window;
            if(captureRoot is not null){var storage=new Storage(Path.Combine(captureRoot,"capture-state"));storage.SaveSettings(new(){OutputFolder=Path.GetFullPath(Path.Combine(captureRoot,"output")),NotifyComplete=false});window=new MainWindow(storage);}else window=new MainWindow();
            if(desktop.Args?.Contains("--dark")==true)RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;
            if(desktop.Args?.Contains("--macos9")==true)Skin.Apply("MacOS9");
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--capture") == true)
            {
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(1000);
                    var root = captureRoot!;
                    Directory.CreateDirectory(root);
                    await Capture(window, Path.Combine(root, "main.png"));
                    var clipIndex = Array.IndexOf(desktop.Args, "--quick-clip");
                    if (clipIndex >= 0 && desktop.Args.Length > clipIndex + 1)
                    {
                        var clip = new EditorWindow(window.Engine, desktop.Args[clipIndex + 1], new(), "quick-workflow");
                        clip.SetWorkflowStep(1,1);clip.Show(window);await clip.Ready;await Task.Delay(300);
                        await Capture(clip,Path.Combine(root,"quick-clip.png"));var edit=clip.ReadClipEdit();clip.Close();
                        var export = new ClipExportWindow([edit],Path.Combine(root,"output"));export.Show(window);await Task.Delay(200);
                        await Capture(export,Path.Combine(root,"quick-clip-export.png"));export.Close();
                    }
                    var rotateIndex = Array.IndexOf(desktop.Args, "--batch-rotate");
                    if (rotateIndex >= 0 && desktop.Args.Length > rotateIndex + 1)
                    {
                        var rotate = new BatchRotateWindow(window.Engine, Path.Combine(root, "output"), [desktop.Args[rotateIndex + 1]]);
                        rotate.Show(window); await rotate.Ready;
                        if (desktop.Args.Contains("--detect-orientation")) await rotate.DetectDirectionsAsync();
                        await Task.Delay(300);
                        await Capture(rotate, Path.Combine(root, "batch-rotate.png"));
                        await File.WriteAllTextAsync(Path.Combine(root, "orientation-verification.json"),
                            System.Text.Json.JsonSerializer.Serialize(rotate.Entries.Select(e => new { e.Name, e.Rotation, e.Detection, e.Error }),
                                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        rotate.Close();
                    }
                    var index = Array.IndexOf(desktop.Args, "--editor");
                    if (index >= 0 && desktop.Args.Length > index + 1)
                    {
                        var editor = new EditorWindow(window.Engine, desktop.Args[index + 1], new ConversionOptions());
                        editor.Show(window);
                        await editor.Ready;
                        await Task.Delay(700);
                        await Capture(editor, Path.Combine(root, "editor.png"));
                        if(desktop.Args.Contains("--verify-ui"))
                        {
                            try {var result=await editor.VerifyPreview();await File.WriteAllTextAsync(Path.Combine(root,"ui-verification.json"),System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));}
                            catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(root,"ui-error.txt"),ex.ToString());editor.Close();desktop.Shutdown(1);return;}
                        }
                        editor.Close();
                    }
                    desktop.Shutdown();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static Task Capture(Avalonia.Controls.Window window, string path) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
    }).GetTask();
}
