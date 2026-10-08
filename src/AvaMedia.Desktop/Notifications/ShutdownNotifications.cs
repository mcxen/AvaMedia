using Avalonia.Controls;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Notifications;

internal static class ShutdownNotifications
{
    public static async Task<bool> WaitAsync(Window owner, CancellationToken ct, int seconds = 30)
    {
        var key = "shutdown:" + Guid.NewGuid().ToString("N"); var remaining = seconds;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Cancel() => completed.TrySetResult(false);
        var cancel = new NotificationAction("取消关机", () => { Cancel(); return Task.CompletedTask; }, Primary: true);
        void Publish() => NotificationCenter.Shared.Publish(owner, new(key, "转换完成后关闭电脑",
            (FormattableString)$"本次任务已全部成功。电脑将在 {remaining} 秒后关闭。\n可以取消关机，继续使用电脑。",
            NotificationKind.Warning, [cancel], OnDismiss: Cancel, Active: true), select: remaining == seconds);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (--remaining <= 0) completed.TrySetResult(true); else Publish(); };
        using var registration = ct.Register(Cancel);
        Publish(); timer.Start();
        try { return await completed.Task; }
        finally
        {
            timer.Stop();
            NotificationCenter.Shared.Publish(owner, new(key, "转换完成后关闭电脑", completed.Task.Result ? "正在关闭电脑…" : "已取消关机",
                NotificationKind.Information), show: false);
        }
    }
}
