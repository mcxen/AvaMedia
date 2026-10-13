using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal sealed record UpdateCheckState(bool Checking = false, UpdateResult? Result = null, DateTimeOffset? CheckedAt = null);
internal sealed record UpdateStatus(string Title, object Message, string? Action = null, bool Busy = false,
    double? Percent = null, bool Error = false, bool Success = false);

internal sealed partial class ApplicationUpdater
{
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private volatile UpdateCheckState _checkState = new();
    private Task? _scheduledChecks;
    private string? _installedVersion, _installationError, _installationLog;
    public UpdateCheckState CheckState => _checkState;
    public UpdateResult? LatestResult => _checkState.Result;
    public string? PreparedVersion => _pending?.Version;
    public Uri? ReleasePage => _pending is { } pending
        ? new("https://github.com/mcxen/AvaMedia/releases/tag/v" + pending.Version) : LatestResult?.ReleasePage;
    public string LogPath => _pending is { } pending ? Marker(pending, "install.log") : _installationLog ?? Root;
    public bool HasLog => File.Exists(LogPath) || Directory.Exists(LogPath);
    public bool CanRestart => !_exiting && IsPrepared && Progress.Phase == UpdatePhase.Ready
        && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        && (desktop.MainWindow is not MainWindow main || main.CanExitForUpdate);
    public string? RestartBlockedReason => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
        { MainWindow: MainWindow main } ? main.UpdateRestartBlockedReason : null;
    public string MenuTitle => Progress.IsBusy ? "正在下载或准备更新…" : Progress.Phase == UpdatePhase.Failed ? "更新失败…"
        : IsPrepared ? "更新已准备好…" : _checkState.Checking ? "正在检查更新…" : _installationError is not null || LatestResult?.CheckSucceeded == false
        ? "更新失败…" : LatestResult?.HasUpdate == true ? "有新版本可用…" : "软件更新…";

    public UpdateStatus Status
    {
        get
        {
            var progress = Progress;
            if (progress.IsBusy)
            {
                var title = progress.Phase switch { UpdatePhase.Downloading => "正在下载更新…",
                    UpdatePhase.Retrying => "正在重试下载更新…", _ => "正在准备更新…" };
                object message = progress.Phase == UpdatePhase.Retrying ? "网络暂时不可用，将自动重试下载。"
                    : progress.Phase == UpdatePhase.Preparing ? "正在校验安装包并准备安装。"
                    : (FormattableString)$"{progress.ReceivedBytes / 1048576d:0.0} / {progress.TotalBytes / 1048576d:0.0} MB";
                return new(title, message, "停止下载", true,
                    progress.Phase == UpdatePhase.Downloading && progress.TotalBytes > 0
                        ? Math.Clamp(100d * progress.ReceivedBytes / progress.TotalBytes, 0, 100) : null);
            }
            if (IsPrepared)
                return progress.Phase == UpdatePhase.Failed
                    ? new("更新安装失败", progress.Error ?? "更新失败", "重试安装", Error: true)
                    : new("更新已准备好", RestartBlockedReason ?? "重启后安装更新。关闭到托盘不会安装；其他应用实例也需退出。", "重启并更新", Success: true);
            if (progress.Phase == UpdatePhase.Failed)
                return new("更新失败", progress.Error ?? "更新失败", LatestResult?.Asset is not null && CanInstall ? "重试下载" : null, Error: true);
            if (_checkState.Checking) return new("正在检查更新…", "正在获取最新版本信息。", Busy: true);
            if (_installationError is { } error) return new("更新安装失败", error, Error: true);
            if (LatestResult is { CheckSucceeded: false } failed)
                return new("检查更新失败", failed.Message, Error: true);
            if (LatestResult is { HasUpdate: true } result)
                return new("发现新版本", (FormattableString)$"新版本 {result.LatestVersion} 可用，当前版本 {AppIdentity.Version}。",
                    result.Asset is not null && CanInstall ? "下载更新" : null);
            if (_installedVersion is { } version) return new("更新安装成功", (FormattableString)$"已更新至 {version}。", Success: true);
            return LatestResult is { CheckSucceeded: true } ? new("已是最新版本", (FormattableString)$"当前版本 {AppIdentity.Version}。", Success: true)
                : new("尚未检查更新", "检查后可查看新版本和更新说明。");
        }
    }

    public async Task<UpdateResult> CheckAsync(Func<CancellationToken, Task<UpdateResult>> check, CancellationToken ct)
    {
        await _checkGate.WaitAsync(ct);
        _checkState = _checkState with { Checking = true }; NotifyStateChanged();
        try
        {
            var result = await check(ct);
            _checkState = new(false, result, DateTimeOffset.Now);
            NotifyStateChanged();
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _checkState = new(false, new(false, error.Message, CheckSucceeded: false), DateTimeOffset.Now);
            NotifyStateChanged();
            throw;
        }
        finally
        {
            if (_checkState.Checking) { _checkState = _checkState with { Checking = false }; NotifyStateChanged(); }
            _checkGate.Release();
        }
    }

    private void NotifyStateChanged()
    {
        if (ProgressChanged is not { } changed) return;
        foreach (EventHandler subscriber in changed.GetInvocationList())
            try { subscriber(this, EventArgs.Empty); }
            catch (Exception error) { AppDiagnostics.Record("Update state observer", error); }
    }

    private void ScheduleDailyChecks()
    {
        if (_scheduledChecks is not null) return;
        _scheduledChecks = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromDays(1), _lifetime.Token);
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (!_exiting && !IsPrepared && !IsDownloading && !CheckState.Checking && _startupSettings is { } settings
                            && (settings.CheckForUpdates || settings.AutoUpdate) && _startupCheck is { } check
                            && _startupOwner?.TryGetTarget(out var owner) == true)
                            await StartupAsync(owner, settings, check, _lifetime.Token);
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { AppDiagnostics.Record("Scheduled update check", error); }
        });
    }

    public async Task RestartAsync()
    {
        if (!CanRestart || _pending is not { } pending) return;
        try
        {
            await EnsureInstallerAsync(pending, _lifetime.Token);
            if (!CanRestart || _pending?.Package != pending.Package) return;
            File.WriteAllText(Marker(pending, "restart"), "");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            { if (desktop.MainWindow is MainWindow main) main.RequestExit(); else desktop.Shutdown(); }
        }
        catch (OperationCanceledException) { CancelRestart(); throw; }
        catch (Exception error)
        {
            CancelRestart(); ReportProgress(Progress with { Phase = UpdatePhase.Failed, Error = error.Message }); throw;
        }
    }

    public void CancelRestart()
    {
        try { if (_pending is { } pending) File.Delete(Marker(pending, "restart")); }
        catch (Exception error) { AppDiagnostics.Record("Cancel update restart", error); }
    }

    public void OpenLog() => Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });

    private string? _lastRestartBlock;
    public void RefreshRestartAvailability()
    {
        if (!IsPrepared) return;
        var reason = RestartBlockedReason;
        if (_lastRestartBlock == reason) return;
        _lastRestartBlock = reason; NotifyStateChanged();
    }

    private async Task ReadInstallationOutcomeAsync(CancellationToken ct)
    {
        if (!TryTarget(out var target, out _) || !Directory.Exists(Root)) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var directory in Directory.EnumerateDirectories(Root).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var marker = Path.Combine(directory, "installed-version");
            var targetFile = Path.Combine(directory, "target.txt");
            if (!File.Exists(targetFile) || !string.Equals(await File.ReadAllTextAsync(targetFile, ct), target, comparison)) continue;
            _installationLog ??= Path.Combine(directory, "install.log");
            if (!File.Exists(marker)) continue;
            var version = (await File.ReadAllTextAsync(marker, ct)).Trim();
            if (Version.TryParse(version, out var installed) && installed <= Version.Parse(AppIdentity.Version))
            {
                File.Delete(marker);
                if (installed == Version.Parse(AppIdentity.Version))
                { _installedVersion = version; _installationLog = Path.Combine(directory, "install.log"); }
            }
        }
        if (File.Exists(ErrorPath))
        {
            _installationError = await File.ReadAllTextAsync(ErrorPath, ct);
            File.Delete(ErrorPath);
        }
        NotifyStateChanged();
    }
}
