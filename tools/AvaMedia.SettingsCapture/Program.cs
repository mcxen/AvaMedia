using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "/workspace/AvaMedia-sidebar-review/settings/impl");
        var categoryFilter = args.FirstOrDefault(arg => arg.StartsWith("--category=", StringComparison.Ordinal))?[11..];
        Directory.CreateDirectory(output);
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithoutStarting();
        Localization.Apply("zh-CN"); Motion.SetReducedMotion(true);
        using var lifetime = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                string[] categories = ["general", "appearance", "player", "convert", "models", "ai", "remote", "advanced", "about"];
                int[] heights = [776, 640, 640, 640, 880, 820, 640, 640, 640];
                var captures = new List<object>();
                var themeFilter = args.FirstOrDefault(arg => arg.StartsWith("--theme=", StringComparison.Ordinal))?[8..];
                foreach (var theme in new[] { "Light", "Dark" }.Where(name => themeFilter is null || name == themeFilter))
                {
                    Skin.Apply(theme);
                    var settings = new AppSettings { Theme = theme, Language = "zh-CN" };
                    var window = new SettingsWindow(settings) { Topmost = true };
                    window.Show(); window.Position = new PixelPoint(80, 80);
                    try
                    {
                        var tabs = window.FindControl<TabControl>("SettingsTabs")!;
                        for (var index = 0; index < categories.Length; index++)
                        {
                            if (categoryFilter is not null && categories[index] != categoryFilter) continue;
                            window.Height = heights[index]; tabs.SelectedIndex = index; window.Activate();
                            await Task.Delay(500);
                            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
                            var size = window.ClientSize; var position = window.PointToScreen(new Point(0, 0));
                            var width = (int)Math.Ceiling(size.Width * window.RenderScaling);
                            var height = (int)Math.Ceiling(size.Height * window.RenderScaling);
                            var name = $"settings-v2-{categories[index]}-{theme.ToLowerInvariant()}.png";
                            // Capture X11 pixels: RenderTargetBitmap omits some templated content on this backend.
                            var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true };
                            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "native_capture.py"));
                            start.ArgumentList.Add(Path.Combine(output, name));
                            foreach (var value in new[] { position.X, position.Y, width, height }) start.ArgumentList.Add(value.ToString(CultureInfo.InvariantCulture));
                            using var process = Process.Start(start)!;
                            var error = process.StandardError.ReadToEndAsync();
                            await process.WaitForExitAsync();
                            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
                            captures.Add(new { file = name, theme, width, height });
                            Console.WriteLine("Captured " + name);
                        }
                    }
                    finally { window.Close(); }
                }
                File.WriteAllText(Path.Combine(output, "captures.json"), JsonSerializer.Serialize(captures, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { lifetime.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(lifetime.Token); return Environment.ExitCode;
    }
}
