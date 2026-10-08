using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;
public sealed partial class App : Application
{
    public override void Initialize()
    {
        Localization.Apply(new Storage().LoadSettings().Language);
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsMacOS() && this.TryGetFeature<IActivatableLifetime>() is { } activation)
            activation.Activated += ApplicationActivated;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        AppDiagnostics.AttachDispatcher();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            if (!args.Contains("--capture")) desktop.Exit += (_, _) => ApplicationUpdater.Shared.InstallOnExit();
            if (!args.Contains("--capture") && !args.Contains("--convert") && (args.Contains("--play") || args.Any(File.Exists)))
            {
                var settings = new Storage().LoadSettings();
                Localization.Apply(settings.Language);
                Skin.Apply(args.Contains("--winxp") ? "WindowsXP" : args.Contains("--macos9") ? "MacOS9" : args.Contains("--dark") ? "Dark" : args.Contains("--light") ? "Light" : settings.Theme);
                Motion.SetReducedMotion(settings.ReduceMotion);
                var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)).ToArray();
                var engine = new MediaEngine(settings);
                var player = new PlayerWindow(engine, files);
                desktop.MainWindow = player;
                if (!args.Contains("--player-benchmark"))
                {
                    var options = new AppOptionsServices();
                    var lifetime = new CancellationTokenSource();
                    player.Opened += async (_, _) => await ApplicationUpdater.Shared.StartupAsync(player, settings, options.CheckUpdatesAsync, lifetime.Token);
                    player.Closed += (_, _) => { lifetime.Cancel(); options.Dispose(); };
                }
                InitializeModelInstallation(desktop, player, args);
                var benchmark = Array.IndexOf(args, "--player-benchmark");
                if (benchmark >= 0 && benchmark + 1 < args.Length)
                {
                    var root = Path.GetFullPath(args[benchmark + 1]);
                    player.Opened += async (_, _) =>
                    {
                        var opened = DateTimeOffset.UtcNow;
                        try
                        {
                            await player.Ready;
                            Directory.CreateDirectory(root);
                            await File.WriteAllTextAsync(Path.Combine(root, "startup.json"), System.Text.Json.JsonSerializer.Serialize(new
                            {
                                processMainUtc = Program.StartedUtc, windowOpenedUtc = opened, firstFrameUtc = player.FirstFrameUtc,
                                openToFirstFrameMs = player.FirstFrameLatencyMs, source = player.CurrentPath, ffmpeg = engine.FFmpeg, error = player.PlaybackError
                            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                            await Task.Delay(150);
                            if (!args.Contains("--player-benchmark-no-capture")) await Capture(player, Path.Combine(root, "player.png"));
                            player.Close(); desktop.Shutdown(string.IsNullOrEmpty(player.PlaybackError) ? 0 : 1);
                        }
                        catch (Exception ex) { Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, "error.txt"), ex.ToString()); player.Close(); desktop.Shutdown(1); }
                    };
                }
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var captureRoot=desktop.Args?.Contains("--capture")==true?desktop.Args.SkipWhile(a=>a!="--capture").Skip(1).FirstOrDefault()??"artifacts":null;
            MainWindow window;
            if(captureRoot is not null){var storage=new Storage(Path.Combine(captureRoot,"capture-state"));storage.SaveSettings(new(){OutputFolder=Path.GetFullPath(Path.Combine(captureRoot,"output")),NotifyComplete=false});window=new MainWindow(storage);}else window=new MainWindow();
            if(desktop.Args?.Contains("--dark")==true)RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;
            if(desktop.Args?.Contains("--macos9")==true)Skin.Apply("MacOS9");
            if(desktop.Args?.Contains("--winxp")==true)Skin.Apply("WindowsXP");
            desktop.MainWindow = window;
            InitializeModelInstallation(desktop, window, args);
            if (args.Contains("--convert")) window.Opened += async (_, _) => await window.ImportForConversionAsync(args);
            if (desktop.Args?.Contains("--capture") == true)
            {
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(1000);
                    var root = captureRoot!;
                    Directory.CreateDirectory(root);
                    await Capture(window, Path.Combine(root, "main.png"));
                    if(desktop.Args.Contains("--download"))await CaptureDownloadAsync(window,root);
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
    private void ApplicationActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind == ActivationKind.Reopen)
        {
            // Dock reopening is separate from opening a file and must restore a hidden window.
            Dispatcher.UIThread.Post(() =>
            {
                if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
                if (desktop.MainWindow is MainWindow main) main.RestoreBackgroundWindow();
                else if (desktop.MainWindow is { } window)
                {
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                        window.WindowState = Avalonia.Controls.WindowState.Normal;
                    Skin.RestoreWindow(window);
                    window.Show(); window.Activate();
                }
            });
            return;
        }
        FilesActivated(sender, args);
    }

    private void FilesActivated(object? sender, ActivatedEventArgs args)
    {
        if (args is not FileActivatedEventArgs files) return;
        var paths = files.Files.Select(file => file.TryGetLocalPath()).OfType<string>()
            .Where(path => File.Exists(path) && SystemPlayerIntegration.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Distinct(VideoFolderScanner.PathComparer).ToArray();
        if (paths.Length == 0) return;
        // Finder can deliver files during startup; defer until the desktop window exists.
        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
            var player = desktop.Windows.OfType<PlayerWindow>().FirstOrDefault(window => window.CanOpenFiles);
            if (player is null)
            {
                var settings = new Storage().LoadSettings();
                player = new PlayerWindow(new MediaEngine(settings), paths);
                player.Show();
            }
            else
            {
                player.Show();
                player.OpenFiles(paths);
            }
            if (player.WindowState == Avalonia.Controls.WindowState.Minimized)
                player.WindowState = Avalonia.Controls.WindowState.Normal;
            player.Activate();
        });
    }

    private static Task Capture(Avalonia.Controls.Window window, string path) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
    }).GetTask();
}
