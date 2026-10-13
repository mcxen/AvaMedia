using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private readonly DispatcherTimer _updateStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _updatesClosed, _updateActionBusy;
    private int _updateRefreshPosted;

    private void InitializeUpdateStatus()
    {
        ApplicationUpdater.Shared.ProgressChanged += UpdateStateChanged;
        _updateStatusTimer.Tick += (_, _) => { if (AboutTab.IsSelected) RefreshUpdateStatus(); };
        Opened += (_, _) => { RefreshUpdateStatus(); _updateStatusTimer.Start(); };
        Closed += (_, _) =>
        {
            _updatesClosed = true; _updateStatusTimer.Stop();
            ApplicationUpdater.Shared.ProgressChanged -= UpdateStateChanged;
        };
        RefreshUpdateStatus();
    }

    public void OpenSoftwareUpdates() => RevealSetting(UpdateStatusCard);

    private void UpdateStateChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _updateRefreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _updateRefreshPosted, 0);
            if (!_updatesClosed) RefreshUpdateStatus();
        });
    }

    private void RefreshUpdateStatus()
    {
        var updater = ApplicationUpdater.Shared;
        var status = updater.Status;
        UpdateStatusTitle.Text = Localization.Text(status.Title);
        UpdateStatusTitle.Classes.Set("error", status.Error);
        UpdateStatusMessage.Text = Localization.RenderContent(status.Message);
        var version = updater.PreparedVersion ?? updater.LatestResult?.LatestVersion?.ToString(3);
        var size = updater.Progress.TotalBytes > 0 ? updater.Progress.TotalBytes : updater.LatestResult?.Asset?.Size ?? 0;
        UpdateMetadataText.Text = version is not null && size > 0
            ? Localization.Format($"版本 {version} · {size / 1048576d:0.0} MB")
            : Localization.Format($"当前版本 {AppIdentity.Version}。");
        UpdateProgressBar.IsVisible = status.Busy;
        UpdateProgressBar.IsIndeterminate = status.Busy && status.Percent is null;
        UpdateProgressBar.Value = status.Percent ?? 0;
        UpdatePrimaryButton.IsVisible = status.Action is not null;
        UpdatePrimaryButton.Content = status.Action is null ? "" : Localization.Text(status.Action);
        UpdatePrimaryButton.Classes.Set("primary", status.Action != "停止下载");
        UpdatePrimaryButton.IsEnabled = !_updateActionBusy && (status.Action switch
        {
            "重启并更新" => updater.CanRestart,
            "停止下载" => updater.CanCancelDownload,
            _ => !updater.IsDownloading
        });
        CheckUpdatesButton.IsEnabled = !_updateActionBusy && !updater.CheckState.Checking && !updater.IsDownloading;
        UpdateReleaseButton.IsVisible = updater.ReleasePage is not null;
        UpdateLogButton.IsVisible = updater.HasLog && (status.Error || File.Exists(updater.LogPath));
        var notes = updater.LatestResult?.ReleaseNotes;
        if (updater.IsPrepared && updater.LatestResult?.LatestVersion?.ToString(3) != updater.PreparedVersion) notes = null;
        UpdateReleaseNotes.IsVisible = !string.IsNullOrWhiteSpace(notes);
        if (UpdateReleaseNotesText.Text != notes) UpdateReleaseNotesText.Text = notes;
        RefreshLastUpdateCheck();
    }

    private async void UpdatePrimaryClick(object? sender, RoutedEventArgs args)
    {
        if (_updateActionBusy) return;
        var updater = ApplicationUpdater.Shared;
        var action = updater.Status.Action;
        _updateActionBusy = true; RefreshUpdateStatus();
        try
        {
            switch (action)
            {
                case "停止下载": updater.CancelDownload(); break;
                case "重启并更新": await updater.RestartAsync(); break;
                case "重试安装": await updater.RetryInstallerAsync(); break;
                case "下载更新" or "重试下载" when updater.LatestResult is { } result:
                    Notifications.UpdateNotifications.Show(this, result);
                    await updater.PrepareAsync(result, automatic: false, CancellationToken.None);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppDiagnostics.Record("Software update action", error);
            if (!_updatesClosed) Notifications.UpdateNotifications.Error(this, "更新失败", error.Message);
        }
        finally { _updateActionBusy = false; if (!_updatesClosed) RefreshUpdateStatus(); }
    }

    private void UpdateReleaseClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (ApplicationUpdater.Shared.ReleasePage is { } page)
                Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) { Notifications.UpdateNotifications.Error(this, "操作失败", error.Message); }
    }

    private void UpdateLogClick(object? sender, RoutedEventArgs args)
    {
        try { ApplicationUpdater.Shared.OpenLog(); }
        catch (Exception error) { Notifications.UpdateNotifications.Error(this, "操作失败", error.Message); }
    }
}
