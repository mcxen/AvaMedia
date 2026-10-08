using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class ModelInstallation
{
    private static readonly LaMaModelInstaller Installer = new();
    private static Task? _running;
    private static bool _attemptActive;
    private static int _attempt;
    private static CancellationTokenSource? _cancellation;
    public static bool Installing { get; private set; }
    public static bool Failed { get; private set; }
    public static int Percent { get; private set; }
    public static string Stage { get; private set; } = "校验模型";
    public static long Received { get; private set; }
    public static long Total { get; private set; }
    public static event Action? Changed;
    public static CancellationToken Lifetime { get; set; }
    public static void Cancel() { _cancellation?.Cancel(); Failed=false; Changed?.Invoke(); }

    public static Task StartAsync()
    {
        if (_running is not null) return _running;
        _attemptActive = true;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        return _running = InstallAsync(++_attempt);
    }
    private static async Task InstallAsync(int attempt)
    {
        await Task.Yield();
        Failed = false;
        Installing = true; Percent = 0; Stage = "校验模型"; Received = Total = 0;
        Changed?.Invoke();
        try
        {
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                if (Lifetime.IsCancellationRequested || !_attemptActive || attempt != _attempt) return;
                Percent = value.Percent; Stage = value.Stage; Received = value.Received; Total = value.Total; Changed?.Invoke();
            });
            await Installer.EnsureInstalledAsync(progress, _cancellation!.Token);
        }
        catch (OperationCanceledException) when (_cancellation!.IsCancellationRequested) { Failed = false; }
        catch (Exception error)
        {
            Failed = true;
            System.Diagnostics.Trace.TraceWarning("图片修复模型安装失败：{0}", error);
        }
        finally
        {
            _attemptActive = false; Installing = false; _running = null;
            _cancellation?.Dispose(); _cancellation=null;
            Changed?.Invoke();
        }
    }
}
