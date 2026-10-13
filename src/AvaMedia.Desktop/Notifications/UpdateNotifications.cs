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
    private static object? _lastFailure;
    private static bool Silent => _automatic && _settings is { AutoUpdate: true, SilentUpdate: true };
    static UpdateNotifications() => ApplicationUpdater.Shared.ProgressChanged += (_, _) => Dispatcher.UIThread.Post(() => Refresh());

    public static void Show(Window owner, UpdateResult result, AppSettings? settings = null, bool automatic = false)
    {
        _result = result; _owner = new(owner); _settings = settings; _automatic = automatic; Refresh(reopen: !automatic);
    }
    public static void Error(Window owner, string title, string message)
        => NotificationCenter.Shared.Publish(owner, new("application-update-error", title, message, NotificationKind.Error,
            [new("查看更新", OpenUpdatesAsync), new("查看日志", () => { ApplicationUpdater.Shared.OpenLog(); return Task.CompletedTask; }, Enabled: () => ApplicationUpdater.Shared.HasLog)]), reopen: true);

    public static void Installed(Window owner, string version)
        => NotificationCenter.Shared.Publish(owner, new("application-update-installed", "更新安装成功",
            (FormattableString)$"已更新至 {version}。", NotificationKind.Success, [new("查看更新", OpenUpdatesAsync)]));

    private static Task OpenUpdatesAsync() => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
        { MainWindow: MainWindow main } ? main.OpenSoftwareUpdatesAsync() : Task.CompletedTask;

    public static bool CheckCompleted(UpdateResult result)
    {
        if (_result is null) return false;
        if (!_result.CheckSucceeded) { _result = result; Refresh(); }
        return !_automatic;
    }

    private static void Refresh(bool reopen = false)
    {
        if (_result is null) return;
        var owner = _owner?.TryGetTarget(out var target) == true ? target : null;
        var updater = ApplicationUpdater.Shared;
        var status = updater.Status;
        var newFailure = status.Error && !Equals(_lastFailure, status.Message);
        _lastFailure = status.Error ? status.Message : null;
        if (Silent && !status.Error) return;
        var actions = new List<NotificationAction>();
        switch (status.Action)
        {
            case "停止下载":
                actions.Add(new("停止下载", () => { updater.CancelDownload(); return Task.CompletedTask; }, Enabled: () => updater.CanCancelDownload));
                break;
            case "重试安装": actions.Add(new("重试安装", updater.RetryInstallerAsync, Primary: true)); break;
            case "重启并更新": actions.Add(new("重启并更新", updater.RestartAsync, Primary: true, Enabled: () => updater.CanRestart)); break;
            case "下载更新" or "重试下载" when updater.LatestResult is { } result:
                actions.Add(new(status.Action, () => { _ = DownloadAsync(result); return Task.CompletedTask; },
                    Primary: true, Enabled: () => !_downloading && !updater.IsDownloading));
                break;
        }
        actions.Add(new("查看更新", OpenUpdatesAsync));
        if (status.Error && updater.HasLog) actions.Add(new("查看日志", () => { updater.OpenLog(); return Task.CompletedTask; }));
        if (updater.ReleasePage is { } page) actions.Add(new("打开发布页", () =>
        { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }));
        var kind = status.Error ? NotificationKind.Error : status.Busy ? NotificationKind.Progress
            : status.Success ? NotificationKind.Success : NotificationKind.Information;
        NotificationCenter.Shared.Publish(owner, new("application-update", status.Title, status.Message, kind, actions,
            status.Percent, status.Busy && status.Percent is null), reopen: reopen || newFailure);
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
}
