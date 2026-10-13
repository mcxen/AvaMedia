using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

internal static partial class Ui
{
    private static Window? _aboutWindow;

    internal static Task AboutAsync(Window? owner)
    {
        if (_aboutWindow is { } existing) { existing.Activate(); return Task.CompletedTask; }
        var dialog = new Window
        {
            Title = "关于 AvaMedia", Width = 480, Height = 380, CanResize = false,
            CanMinimize = false, CanMaximize = false, WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        using var stream = AssetLoader.Open(new Uri("avares://AvaMedia.Desktop/Assets/AppIcon/v2/app.png"));
        var icon = new Bitmap(stream);
        var body = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12, Margin = new(18) };
        var info = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        var appIcon = new Image { Source = icon, Width = 80, Height = 80, HorizontalAlignment = HorizontalAlignment.Center };
        HookExclusiveToolsActivation(appIcon, dialog, UnlockExclusiveTools);
        info.Children.Add(appIcon);
        TextBlock Center(string value, string? role = null)
        {
            var text = ComponentText(value, role);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.TextAlignment = Avalonia.Media.TextAlignment.Center;
            return text;
        }
        info.Children.Add(Center("AvaMedia", "heading"));
        info.Children.Add(Center("版本 " + typeof(App).Assembly.GetName().Version, "caption"));
        info.Children.Add(Center("版权所有 © 2026 AvaMedia contributors。", "caption"));
        info.Children.Add(Center("AGPL-3.0-only · 开源多媒体工具", "caption"));
        var github = Button("GitHub · 源代码与许可证", () =>
            Process.Start(new ProcessStartInfo("https://github.com/mcxen/AvaMedia") { UseShellExecute = true }));
        github.HorizontalAlignment = HorizontalAlignment.Center;
        info.Children.Add(github);
        body.Children.Add(new ScrollViewer { Content = info });
        var close = DialogButton("好", () => dialog.Close(), isDefault: true);
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(close, 1); body.Children.Add(close);
        dialog.Content = WindowChrome.WrapDialog(dialog, body);
        _aboutWindow = dialog;
        dialog.Closed += (_, _) => { _aboutWindow = null; icon.Dispose(); };
        if (owner is not null) return dialog.ShowDialog(owner);
        dialog.Show();
        return Task.CompletedTask;
    }
}
