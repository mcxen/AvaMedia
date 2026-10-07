using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class ModelInstallation
{
    private static readonly LaMaModelInstaller Installer = new();
    private static Task? _running;
    private static bool _attemptActive;
    private static int _attempt;
    public static bool Installing { get; private set; }
    public static bool Failed { get; private set; }
    public static int Percent { get; private set; }
    public static event Action? Changed;
    public static CancellationToken Lifetime { get; set; }

    public static Task StartAsync()
    {
        if (_running is not null) return _running;
        _attemptActive = true;
        return _running = InstallAsync(++_attempt);
    }
    private static async Task InstallAsync(int attempt)
    {
        await Task.Yield();
        Failed = false;
        Changed?.Invoke();
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (Lifetime.IsCancellationRequested || !_attemptActive || attempt != _attempt) return;
                Installing = percent < 100; Percent = percent; Changed?.Invoke();
            });
            await Installer.EnsureInstalledAsync(progress, Lifetime);
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Failed = true;
            System.Diagnostics.Trace.TraceWarning("图片修复模型安装失败：{0}", error);
        }
        finally
        {
            _attemptActive = false; Installing = false; _running = null;
            Changed?.Invoke();
        }
    }
}
