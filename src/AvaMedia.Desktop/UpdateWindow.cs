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
        var panel = new StackPanel { Margin = new(22), Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = result.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        var release = new Button { Name = "OpenReleaseButton", Content = "打开发布页", IsVisible = result.HasUpdate && result.ReleasePage is not null };
        release.Click += (_, _) => { if (result.ReleasePage is {} page) Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); };
        buttons.Children.Add(release); buttons.Children.Add(Ui.DialogButton("确定", () => Close())); panel.Children.Add(buttons); Content = panel;
    }
}
