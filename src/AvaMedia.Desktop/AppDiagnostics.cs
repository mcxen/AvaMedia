using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class AppDiagnostics
{
    private static readonly object WriteLock = new();
    internal static void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) Record("Unhandled exception", error);
        };
        Dispatcher.UIThread.UnhandledException += (_, args) => Record("UI dispatcher", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => Record("Unobserved task", args.Exception);
    }

    internal static void Record(string operation, Exception error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "logs");
            lock (WriteLock)
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "exceptions.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} · AvaMedia {AppIdentity.Version} · PID {Environment.ProcessId} · {operation}\n{error}\n\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
