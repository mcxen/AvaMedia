using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public enum UiSound { Operation, Complete, Error }

public interface IAppOptionsServices : IDisposable
{
    bool CanUseTray { get; }
    bool CanUseContextMenu { get; }
    void SetContextMenu(bool enabled);
    void SetTray(bool enabled, Action restore, Action exit);
    void PlaySound(UiSound sound);
    void OpenFolder(string path);
    Task ShutdownAsync(CancellationToken ct);
    Task<UpdateResult> CheckUpdatesAsync(CancellationToken ct);
}

public sealed class AppOptionsServices : IAppOptionsServices
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private TrayIcon? _tray;
    public bool CanUseTray => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    public bool CanUseContextMenu => (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && File.Exists(Executable);
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "AvaMedia.Desktop" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    public void SetContextMenu(bool enabled) => SystemContextMenu.Set(enabled, Executable);
    public void SetTray(bool enabled, Action restore, Action exit)
    {
        if (!enabled) { DisposeTray(); return; }
        if (!CanUseTray) throw new PlatformNotSupportedException("当前环境不提供系统托盘。窗口仍会正常最小化。");
        if (_tray is not null) return;
        var menu = new NativeMenu();
        var show = new NativeMenuItem("显示 AvaMedia"); show.Click += (_, _) => restore();
        var quit = new NativeMenuItem("退出"); quit.Click += (_, _) => exit();
        menu.Items.Add(show); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
        using var icon = AssetLoader.Open(new Uri("avares://AvaMedia.Desktop/Assets/AppIcon/v2/app.ico"));
        _tray = new() { Icon = new WindowIcon(icon), Menu = menu, ToolTipText = "AvaMedia", IsVisible = true };
        _tray.Clicked += (_, _) => restore();
        var icons = TrayIcon.GetIcons(Application.Current!) ?? new TrayIcons();
        icons.Add(_tray); TrayIcon.SetIcons(Application.Current!, icons);
    }
    private void DisposeTray()
    {
        if (_tray is null) return;
        var icons = Application.Current is {} app ? TrayIcon.GetIcons(app) : null;
        if (icons?.Remove(_tray) != true) _tray.Dispose();
        _tray = null;
    }
    public void PlaySound(UiSound sound) { if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime) UiSounds.Play(sound); }
    public void OpenFolder(string path) => PlatformServices.OpenFolder(path);
    public async Task ShutdownAsync(CancellationToken ct)
    {
        var result = OperatingSystem.IsWindows()
            ? await ProcessRunner.Run("shutdown.exe", ["/s", "/t", "0"], ct)
            : OperatingSystem.IsMacOS()
                ? await ProcessRunner.Run("/usr/bin/osascript", ["-e", "tell application \"System Events\" to shut down"], ct)
                : throw new PlatformNotSupportedException("当前平台不支持自动关机。");
        if (result.ExitCode != 0) throw new IOException("系统未能关机：" + result.Error);
    }
    public Task<UpdateResult> CheckUpdatesAsync(CancellationToken ct) => new ReleaseUpdateClient(_http)
        .CheckAsync(typeof(AppOptionsServices).Assembly.GetName().Version ?? new Version(1, 0, 0), ct);
    public void Dispose() { DisposeTray(); _http.Dispose(); }
}
