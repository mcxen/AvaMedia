using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class App
{
    private void StartMcpBackground(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        string? Value(string option)
        {
            var index = Array.IndexOf(args, option);
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException("缺少参数：" + option);
            return args[index + 1];
        }
        int? port = Value("--mcp-port") is { } text
            ? int.TryParse(text, out var number) && number is >= 1024 and <= 65535 ? number : throw new ArgumentException("MCP 端口须为 1024–65535。")
            : null;
        var storage = new Storage(Value("--mcp-state-directory") is { } root ? Path.GetFullPath(root) : null);
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = _mcpBackgroundWindow = new MainWindow(storage);
        // Keep the normal owner/dispatcher/queue, without assigning a MainWindow that Avalonia would show.
        window.Closed += (_, _) => desktop.Shutdown();
        void Exit() => Dispatcher.UIThread.Post(window.RequestExit);
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; Exit(); };
        Console.CancelKeyPress += cancel;
        var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            context => { context.Cancel = true; Exit(); });
        desktop.Exit += (_, _) =>
        {
            Console.CancelKeyPress -= cancel; signal?.Dispose();
            ModelDownloads.Shared.Shutdown(); Notifications.NotificationCenter.Shared.Shutdown();
            _mcpBackgroundWindow = null;
        };
        Dispatcher.UIThread.Post(async () =>
        {
            try { await window.StartMcpBackgroundAsync(port); }
            catch (Exception error)
            {
                AppDiagnostics.Record("Background MCP startup", error);
                Console.Error.WriteLine(error.Message); desktop.Shutdown(1);
            }
        });
    }
}
