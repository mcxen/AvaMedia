using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

internal static partial class Program
{
    // Targeted previews only; no task submission, AI analysis or media export.
    private static async Task CaptureAmbient()
    {
        var video = Path.Combine(_samples, "航天员.mp4");
        var image = Path.Combine(_samples, "航天员.png");
        foreach (var file in new[] { video, image })
            if (!File.Exists(file)) throw new FileNotFoundException("Missing screenshot sample", file);
        foreach (var index in Enumerable.Range(0, Themes.Length))
        {
            var theme = Themes[index]; var name = Names[index];
            var state = new Storage(Path.Combine(_output, "state", name));
            var settings = new AppSettings { Theme = theme, Language = "zh-CN", CloseToTray = false,
                CheckForUpdates = false, AutoUpdate = false, AutoDownloadRepairModel = false,
                ReduceMotion = true, PlayerNativeHighResolution = false, EnableBetaFeatures = true,
                OutputFolder = Path.Combine(_output, "output") };
            state.SaveSettings(settings);
            var main = new MainWindow(state); main.Show(); await Settle(main);
            var engine = main.Engine;

            if (!_ambientSharedOnly)
            {
                var editor = new EditorWindow(engine, video, new() { Start = .5, End = 3.5 }, "quick-workflow");
                editor.Show(main); await editor.Ready; await editor.PreviewReady; await editor.ThumbnailsReady;
                await Capture(editor, "editor-" + name);
                var clip = editor.ReadClipEdit(); editor.Close();
                var crop = new BatchCropWindow(engine, settings.OutputFolder, [video]);
                crop.Show(main); await crop.Ready; await Capture(crop, "crop-" + name); crop.Close();
                var rotate = new BatchRotateWindow(engine, settings.OutputFolder, [video]);
                rotate.Show(main); await rotate.Ready; await Capture(rotate, "rotate-" + name); rotate.Close();
                var export = new ClipExportWindow([clip], settings.OutputFolder);
                export.Show(main); await Capture(export, "export-" + name); export.Close();
            }
            await PanelCapture(new ClipSplitWindow(new() { Start = .5, End = 3.5 }, 4, engine, video), "split");
            await PanelCapture(new VideoCompressionWindow(engine, settings.OutputFolder, [video]), "video-compression");
            await PanelCapture(new ConvertWindow(engine, Catalog.Find("mp4"), settings.OutputFolder, [video]), "convert");
            var compression = new ImageCompressionWindow(engine, settings.OutputFolder, [image]);
            compression.Show(main); await compression.Ready;
            await Capture(compression, "image-compression-" + name); compression.Close();
            await PanelCapture(new PersonClipWindow(engine, settings, [video], (_, _) => Task.CompletedTask), "person");
            if (!_ambientSharedOnly)
            {
                var player = new PlayerWindow(engine, [video], preferences: state);
                player.Show(main); await player.Ready; await player.FirstFrameReady.WaitAsync(TimeSpan.FromSeconds(15));
                if (player.IsPlaying) await player.TogglePlaybackAsync();
                await Capture(player, "player-windowed-" + name);
                player.WindowState = WindowState.FullScreen;
                await Capture(player, "player-fullscreen-" + name); player.Close();
            }
            CloseMain(main);

            async Task PanelCapture(Window window, string label)
            {
                window.Show(main);
                // Shared panels expose no Ready task; wait only for their real source image.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!window.GetVisualDescendants().OfType<ImageCompareView>().Any(view => view.Source is not null))
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException(label + " preview did not load");
                    await Task.Delay(50);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                await Capture(window, label + "-" + name); window.Close();
            }
        }
        await File.WriteAllTextAsync(Path.Combine(_output, "captures.json"), JsonSerializer.Serialize(new
        {
            capturedUtc = DateTimeOffset.UtcNow, captures = Captures,
            mediaExportsExecuted = false, aiAnalysisExecuted = false,
            previewDecoration = "neutral-shadow", continuousAmbientSampling = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
