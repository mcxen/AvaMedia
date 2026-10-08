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
    public static string? Error { get; private set; }
    public static int Percent { get; private set; }
    public static string Stage { get; private set; } = "校验模型";
    public static long Received { get; private set; }
    public static long Total { get; private set; }
    public static int RetryAttempt { get; private set; }
    public static int MaxAttempts { get; private set; }
    public static bool CancellationRequested => _cancellation?.IsCancellationRequested == true;
    public static event Action? Changed;
    public static CancellationToken Lifetime { get; set; }
    public static void Cancel() { _cancellation?.Cancel(); Failed = false; Error = null; Stage = "正在停止…"; NotifyChanged(); }

    public static void ClearFailure()
    {
        if (!Failed) return;
        Failed = false; Error = null; NotifyChanged();
    }

    public static Task StartAsync()
    {
        if (_running is not null) return _running;
        _attemptActive = true;
        return _running = InstallAsync(++_attempt);
    }
    private static async Task InstallAsync(int attempt)
    {
        await Task.Yield();
        try
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
            Failed = false; Error = null;
            Installing = true; Percent = 0; Stage = "校验模型"; Received = Total = 0;
            RetryAttempt = MaxAttempts = 0;
            NotifyChanged();
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                if (Lifetime.IsCancellationRequested || CancellationRequested || !_attemptActive || attempt != _attempt) return;
                Percent = value.Percent; Stage = value.Stage; Received = value.Received; Total = value.Total;
                RetryAttempt = value.Attempt; MaxAttempts = value.MaxAttempts; NotifyChanged();
            });
            await Installer.EnsureInstalledAsync(progress, _cancellation!.Token);
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested || _cancellation?.IsCancellationRequested == true) { Failed = false; }
        catch (Exception error)
        {
            Failed = true; Error = error.Message;
            AppDiagnostics.Record("Repair model installation", error);
        }
        finally
        {
            _attemptActive = false; Installing = false; _running = null;
            _cancellation?.Dispose(); _cancellation=null;
            NotifyChanged();
        }
    }

    private static void NotifyChanged()
    {
        if (Changed is not { } changed) return;
        foreach (Action subscriber in changed.GetInvocationList())
        {
            try { subscriber(); }
            catch (Exception error) { AppDiagnostics.Record("Model installation status", error); }
        }
    }
}
