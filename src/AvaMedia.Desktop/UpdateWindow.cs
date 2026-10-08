using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class UpdateWindow : Window
{
    public UpdateWindow(UpdateResult result, AppSettings? settings = null, Action? saveSettings = null)
    {
        Title = "检测新版本"; Width = 530; Height = settings is null ? 300 : 340;
        MinWidth = 400; MinHeight = settings is null ? 270 : 310; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new Grid { RowDefinitions = new(settings is null ? "*,Auto,Auto" : "*,Auto,Auto,Auto"), Margin = new(22), RowSpacing = 18 };
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = result.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        var message = (TextBlock)((ScrollViewer)panel.Children[0]).Content!;
        var progressBar = new ProgressBar { Name = "UpdateDownloadProgressBar", Minimum = 0, Maximum = 100, Height = 12 };
        var progressText = new TextBlock { Name = "UpdateDownloadProgressText", HorizontalAlignment = HorizontalAlignment.Right };
        var progressPanel = new StackPanel { Spacing = 8, IsVisible = false };
        progressPanel.Children.Add(progressBar); progressPanel.Children.Add(progressText);
        Grid.SetRow(progressPanel, 1); panel.Children.Add(progressPanel);
        if (settings is not null)
        {
            var check = new CheckBox { Name = "CheckUpdatesOnStartupInput", Content = "启动时检查新版本", IsChecked = settings.CheckForUpdates };
            var changing = false;
            check.IsCheckedChanged += (_, _) =>
            {
                if (changing) return;
                var previous = settings.CheckForUpdates;
                settings.CheckForUpdates = check.IsChecked == true;
                try
                {
                    if (saveSettings is not null) saveSettings();
                    else new Storage().SaveSettings(settings);
                }
                catch (Exception error)
                {
                    settings.CheckForUpdates = previous;
                    changing = true;
                    try { check.IsChecked = previous; }
                    finally { changing = false; }
                    Localization.SetText(message, $"更新检查设置保存失败：{error.Message}");
                }
            };
            Grid.SetRow(check, 2); panel.Children.Add(check);
        }
        var install = new Button { Name = "InstallUpdateButton", Content = ApplicationUpdater.Shared.IsPrepared ? "已准备更新" : "下载更新",
            IsVisible = result.HasUpdate && result.Asset is not null && ApplicationUpdater.CanInstall,
            IsEnabled = !ApplicationUpdater.Shared.IsPrepared };
        var updater = ApplicationUpdater.Shared;
        var closed = false;
        void RefreshProgress()
        {
            if (closed || !result.HasUpdate) return;
            var progress = updater.Progress;
            install.IsEnabled = !progress.IsBusy && !updater.IsPrepared;
            install.Content = Localization.Text(progress.Phase == UpdatePhase.Ready ? "已准备更新" :
                progress.Phase == UpdatePhase.Failed ? "重试下载" : "下载更新");
            progressPanel.IsVisible = progress.TotalBytes > 0;
            progressBar.IsIndeterminate = progress.Phase == UpdatePhase.Preparing;
            var percent = progress.TotalBytes > 0 ? 100d * progress.ReceivedBytes / progress.TotalBytes : 0;
            progressBar.Value = percent;
            Localization.SetText(progressText, $"{percent:0}% · {progress.ReceivedBytes / 1048576d:0.0} / {progress.TotalBytes / 1048576d:0.0} MB");
            switch (progress.Phase)
            {
                case UpdatePhase.Downloading: Localization.SetText(message, $"正在下载更新…"); break;
                case UpdatePhase.Preparing: Localization.SetText(message, $"正在准备更新…"); break;
                case UpdatePhase.Ready: Localization.SetText(message, $"更新已准备好，将在退出应用时安装。"); break;
                case UpdatePhase.Failed: message.Text = Localization.Text(progress.Error ?? "更新失败"); break;
                default: message.Text = result.Message; break;
            }
        }
        void ProgressChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(RefreshProgress);
        Opened += (_, _) => { updater.ProgressChanged += ProgressChanged; RefreshProgress(); };
        Closed += (_, _) => { closed = true; updater.ProgressChanged -= ProgressChanged; };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            Localization.SetText(message, $"正在下载更新…");
            try
            {
                // The application owns the download; closing this window only dismisses its progress view.
                await updater.PrepareAsync(result, automatic: false, CancellationToken.None);
                RefreshProgress();
            }
            catch (OperationCanceledException) { RefreshProgress(); }
            catch (Exception ex) { if (!closed) { RefreshProgress(); message.Text = Localization.Text(ex.Message); install.IsEnabled = true; } }
        };
        RefreshProgress();
        buttons.Children.Add(install);
        var release = new Button { Name = "OpenReleaseButton", Content = "打开发布页", VerticalAlignment = VerticalAlignment.Center, IsVisible = result.HasUpdate && result.ReleasePage is not null };
        release.Click += (_, _) => { if (result.ReleasePage is {} page) Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); };
        buttons.Children.Add(release); buttons.Children.Add(Ui.DialogButton("确定", () => Close()));
        Grid.SetRow(buttons, settings is null ? 2 : 3); panel.Children.Add(buttons); Content = panel;
    }
}
