using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace AvaMedia.Desktop;

public sealed class ShutdownCountdownWindow : Window
{
    private int _remaining;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _timer;
    public ShutdownCountdownWindow(int seconds = 30)
    {
        _remaining = seconds; Title = "转换完成后关闭电脑"; Width = 520; Height = 220; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24), Spacing = 24 };
        _message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; panel.Children.Add(_message);
        var cancel = new Button { Name = "CancelShutdownButton", Content = "取消关机", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close(false); panel.Children.Add(cancel); Content = panel;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _timer.Tick += (_, _) => Tick();
        Opened += (_, _) => _timer.Start(); Closed += (_, _) => _timer.Stop(); UpdateText();
    }
    internal void Tick() { if (--_remaining <= 0) Close(true); else UpdateText(); }
    private void UpdateText() => Localization.SetText(_message, $"本次任务已全部成功。电脑将在 {_remaining} 秒后关闭。\n可以取消关机，继续使用电脑。");
}
