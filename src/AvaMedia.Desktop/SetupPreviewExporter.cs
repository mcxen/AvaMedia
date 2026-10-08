using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

// Production artwork, rendered from the real client on each platform during packaging.
internal static class SetupPreviewExporter
{
    internal const string Argument = "--export-setup-previews";
    internal static void Start(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.UIThread.Post(async () =>
        {
            var workspace = Path.Combine(Path.GetTempPath(), "AvaMedia-setup-previews-" + Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(output);
                foreach (var theme in new[] { "Light", "Dark", "MacOS9", "WindowsXP" })
                {
                    window = MainWindow.CreateSetupPreview(Path.Combine(workspace, theme), theme);
                    desktop.MainWindow = window;
                    window.Show();
                    window.UpdateLayout();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    var rows = window.GetVisualDescendants().OfType<Controls.JobRowView>().ToArray();
                    await Task.WhenAll(rows.Select(row => row.Ready));
                    foreach (var row in rows)
                    {
                        if (row.Details is not { } details) continue;
                        details.SetAppearancePreview(details.Job.FeatureId switch
                        {
                            "audio-mp3" => new(125, 0, 0, true, false, "", AudioCodec: "mp3", AudioSampleRate: 44100, AudioChannels: 2),
                            "image-webp" => new(0, 1600, 1200, false, true, "", "png"),
                            _ => new(125, 1920, 1080, true, true, "", "h264", "aac", 48000, 2, FrameRate: 30)
                        });
                    }
                    window.PrepareSetupPreviewForRender();
                    window.UpdateLayout();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    var size = window.ClientSize;
                    using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2)), new Vector(192, 192)))
                    {
                        bitmap.Render(window);
                        var path = Path.Combine(output, theme + ".png");
                        using (var stream = File.Create(path + ".tmp")) bitmap.Save(stream);
                        File.Move(path + ".tmp", path, true);
                    }
                    window.CloseSetupPreview(); window = null;
                }
                desktop.Shutdown();
            }
            catch (Exception error)
            {
                window?.CloseSetupPreview();
                Console.Error.WriteLine(error);
                desktop.Shutdown(1);
            }
            finally
            {
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        });
    }
}
