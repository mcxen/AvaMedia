using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Notifications;

internal static class ModelNotifications
{
    private static Window? _owner;
    private static bool _shown;
    public static void Initialize(Window owner)
    {
        _owner = owner; ModelInstallation.Changed += Changed;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Exit += (_, _) => { ModelInstallation.Changed -= Changed; _owner = null; };
    }
    private static void Changed()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Changed); return; }
        if (ModelInstallation.Lifetime.IsCancellationRequested) return;
        if (ModelInstallation.Installing && ModelInstallation.Stage != "完成"
            && (ModelInstallation.Stage != "校验模型" || ModelInstallation.Received > 0)) _shown = true;
        if (!_shown && !ModelInstallation.Failed) return;
        var key = "repair-model:" + ModelInstallation.Attempt;
        if (ModelInstallation.Installing)
        {
            FormattableString body = $"{Localization.Key(ModelInstallation.Stage)} · {ModelInstallation.Received / 1048576d:0.0} / {ModelInstallation.Total / 1048576d:0.0} MB";
            NotificationCenter.Shared.Publish(_owner, new(key, "图片修复模型", body, NotificationKind.Progress,
                [new("停止下载", () => { ModelInstallation.Cancel(); return Task.CompletedTask; }, Enabled: () => !ModelInstallation.CancellationRequested)],
                ModelInstallation.Total > 0 ? ModelInstallation.Percent : null, ModelInstallation.Total <= 0));
        }
        else if (ModelInstallation.Failed)
        {
            NotificationCenter.Shared.Publish(_owner, new(key, "模型下载失败", ModelInstallation.Error ?? "图片修复暂不可用，请检查网络后重试。",
                NotificationKind.Error, [new("重试下载", () => { _ = ModelInstallation.StartAsync(); return Task.CompletedTask; }, Primary: true, DismissOnSuccess: true), new("模型管理", OpenManagementAsync)]));
            _shown = false;
        }
        else
        {
            NotificationCenter.Shared.Publish(_owner, new(key, ModelInstallation.Succeeded ? "模型已就绪" : "模型下载已停止",
                "图片修复模型", ModelInstallation.Succeeded ? NotificationKind.Success : NotificationKind.Information,
                [new("模型管理", OpenManagementAsync)]), show: _shown);
            _shown = false;
        }
    }
    internal static async Task OpenManagementAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.Windows.OfType<SettingsWindow>().FirstOrDefault() is { } settings)
        { settings.OpenModelManagement(); settings.Activate(); return; }
        if (desktop.MainWindow is MainWindow main) await main.OpenModelManagementAsync();
        else if (desktop.MainWindow is { } owner)
        {
            using var options = new AppOptionsServices(); var preferences = new AvaMedia.Core.Storage().LoadSettings();
            var window = new SettingsWindow(preferences, options); window.OpenModelManagement(); await window.ShowDialog<bool>(owner);
        }
    }
}
