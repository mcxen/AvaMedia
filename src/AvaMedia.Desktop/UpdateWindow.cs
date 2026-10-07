using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class UpdateWindow : Window
{
    public UpdateWindow(UpdateResult result)
    {
        Title = "检测新版本"; Width = 530; Height = 230; MinWidth = 400; MinHeight = 200; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new Grid { RowDefinitions = new("*,Auto"), Margin = new(22), RowSpacing = 18 };
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = result.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        var message = (TextBlock)((ScrollViewer)panel.Children[0]).Content!;
        var install = new Button { Name = "InstallUpdateButton", Content = ApplicationUpdater.Shared.IsPrepared ? "已准备更新" : "下载更新",
            IsVisible = result.HasUpdate && result.Asset is not null && ApplicationUpdater.CanInstall,
            IsEnabled = !ApplicationUpdater.Shared.IsPrepared };
        var lifetime = new CancellationTokenSource();
        Closed += (_, _) => lifetime.Cancel();
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            Localization.SetText(message, $"正在下载更新…");
            try
            {
                await ApplicationUpdater.Shared.PrepareAsync(result, automatic: false, lifetime.Token);
                Localization.SetText(message, $"更新已准备好，将在退出应用时安装。");
                install.Content = Localization.Text("已准备更新");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsVisible) { message.Text = Localization.Text(ex.Message); install.IsEnabled = true; } }
        };
        if (ApplicationUpdater.Shared.IsPrepared) Localization.SetText(message, $"更新已准备好，将在退出应用时安装。");
        buttons.Children.Add(install);
        var release = new Button { Name = "OpenReleaseButton", Content = "打开发布页", VerticalAlignment = VerticalAlignment.Center, IsVisible = result.HasUpdate && result.ReleasePage is not null };
        release.Click += (_, _) => { if (result.ReleasePage is {} page) Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); };
        buttons.Children.Add(release); buttons.Children.Add(Ui.DialogButton("确定", () => Close())); Grid.SetRow(buttons, 1); panel.Children.Add(buttons); Content = panel;
    }
}
