using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Notifications;

internal static class UpdateNotifications
{
    private static UpdateResult? _result;
    private static WeakReference<Window>? _owner;
    private static AppSettings? _settings;
    private static bool _automatic;
    private static bool _downloading;
    private static bool Silent => _automatic && _settings is { AutoUpdate: true, SilentUpdate: true };
    static UpdateNotifications() => ApplicationUpdater.Shared.ProgressChanged += (_, _) => Dispatcher.UIThread.Post(() => Refresh());

    public static void Show(Window owner, UpdateResult result, AppSettings? settings = null, bool automatic = false)
    {
        _result = result; _owner = new(owner); _settings = settings; _automatic = automatic; Refresh(reopen: !automatic);
    }
    public static void Error(Window owner, string title, string message)
        => NotificationCenter.Shared.Publish(owner, new(Guid.NewGuid().ToString("N"), title, message, NotificationKind.Error));

    public static bool CheckCompleted(UpdateResult result)
    {
        if (_result is null) return false;
        if (!_result.CheckSucceeded) { _result = result; Refresh(); }
        return !_automatic;
    }

    private static void Refresh(bool reopen = false)
    {
        if (_result is not { } result || Silent) return;
        var owner = _owner?.TryGetTarget(out var target) == true ? target : null;
        var updater = ApplicationUpdater.Shared; var progress = updater.Progress;
        var actions = new List<NotificationAction>(); object title = "检测新版本", body = result.Message;
        var kind = NotificationKind.Information;
        double? percent = null; var indeterminate = false;
        if (!result.CheckSucceeded) { title = "正在重试更新检查…"; indeterminate = true; kind = NotificationKind.Progress; }
        if (result.HasUpdate)
        {
            title = "发现新版本";
            if (progress.IsBusy || _downloading)
            {
                title = progress.Phase switch
                {
                    UpdatePhase.Downloading => "正在下载更新…",
                    UpdatePhase.Retrying => "正在重试下载更新…",
                    _ => "正在准备更新…"
                };
                kind = NotificationKind.Progress;
                body = progress.Phase == UpdatePhase.Retrying ? "网络暂时不可用，将自动重试下载。" :
                    (FormattableString)$"{progress.ReceivedBytes / 1048576d:0.0} / {progress.TotalBytes / 1048576d:0.0} MB";
                percent = progress.TotalBytes > 0 ? 100d * progress.ReceivedBytes / progress.TotalBytes : null;
                indeterminate = progress.Phase is UpdatePhase.Preparing or UpdatePhase.Retrying || percent is null;
                actions.Add(new("停止下载", () => { updater.CancelDownload(); return Task.CompletedTask; }, Enabled: () => updater.CanCancelDownload));
            }
            else if (updater.IsPrepared)
            {
                title = "更新已准备好"; body = "更新已准备好，将在退出应用时安装。"; kind = NotificationKind.Success;
                actions.Add(new("退出并安装", ExitAsync, Primary: true, Enabled: CanExit));
            }
            else
            {
                if (progress.Phase == UpdatePhase.Failed) { title = "更新失败"; body = progress.Error ?? "更新失败"; kind = NotificationKind.Error; }
                if (result.Asset is not null && ApplicationUpdater.CanInstall)
                    actions.Add(new(progress.Phase == UpdatePhase.Failed ? "重试下载" : "下载更新", () =>
                    { _ = DownloadAsync(result); return Task.CompletedTask; }, Primary: true, Enabled: () => !_downloading && !updater.IsDownloading));
            }
        }
        if (result.ReleasePage is { } page) actions.Add(new("打开发布页", () =>
        { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }));
        NotificationCenter.Shared.Publish(owner, new("application-update", title, body, kind, actions, percent, indeterminate), reopen: reopen);
    }
    private static async Task DownloadAsync(UpdateResult result)
    {
        if (_downloading || ApplicationUpdater.Shared.IsDownloading) return;
        _downloading = true; Refresh();
        try { await ApplicationUpdater.Shared.PrepareAsync(result, automatic: false, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppDiagnostics.Record("Update notification download", error); }
        finally { _downloading = false; Refresh(); }
    }
    private static bool CanExit() => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        && ApplicationUpdater.Shared.IsPrepared && (desktop.MainWindow is not MainWindow main || main.CanExitForUpdate);
    private static Task ExitAsync()
    {
        if (!CanExit()) return Task.CompletedTask;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        { if (desktop.MainWindow is MainWindow main) main.RequestExit(); else desktop.Shutdown(); }
        return Task.CompletedTask;
    }
}
