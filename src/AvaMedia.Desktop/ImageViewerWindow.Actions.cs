using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class ImageViewerWindow
{
    private void ShowFileActions()
    {
        var menu = new ContextMenu();
        void Add(string label, Func<Task> action)
        { var item = new MenuItem { Header = label }; item.Click += async (_, _) => await GuardAsync(action); menu.Items.Add(item); }
        Add("复制原图", () => CopyImageAsync(false)); Add("复制当前视图", () => CopyImageAsync(true));
        Add("复制文件路径", async () => { if (Clipboard is not null && CurrentEntry is { } entry) await Clipboard.SetTextAsync(entry.Identity.Replace('\n', ' ')); });
        Add("复制 EXIF 信息", async () => { if (Clipboard is not null && _document is { } document) await Clipboard.SetTextAsync(string.Join(Environment.NewLine, document.Metadata.Select(item => item.Key + ": " + item.Value))); });
        menu.Items.Add(new Separator());
        Add("复制到…", () => TransferAsync(false)); Add("移动到…", () => TransferAsync(true));
        Add("批量转换…", ExportAsync); Add("在文件夹中显示", RevealAsync); Add("外部编辑器…", EditExternallyAsync);
        Add("打印…", PrintAsync); Add("设为桌面背景", SetWallpaperAsync); Add("移到回收站", RecycleAsync);
        menu.Open(_fileActions);
    }
    private ImageViewerEntry? CurrentEntry => _index >= 0 && _index < _entries.Length ? _entries[_index] : null;
    private async Task CopyImageAsync(bool view)
    {
        if (_document is null || Clipboard is null) return;
        byte[] png;
        if (view)
        {
            if (_viewport.Bounds.Width < 1 || _viewport.Bounds.Height < 1) return;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)_viewport.Bounds.Width, (int)_viewport.Bounds.Height));
            bitmap.Render(_viewport); using var stream = new MemoryStream(); bitmap.Save(stream); png = stream.ToArray();
        }
        else png = _document.Frames[_frame].Png;
        var transfer = new DataTransfer(); var item = new DataTransferItem();
        item.Set(DataFormat.CreateBytesPlatformFormat(OperatingSystem.IsMacOS() ? "public.png" : OperatingSystem.IsWindows() ? "PNG" : "image/png"), png);
        transfer.Add(item); await Clipboard.SetDataAsync(transfer);
    }
    private async Task RecycleAsync()
    {
        if (CurrentEntry is not { } entry) return;
        if (entry.InArchive) throw new InvalidOperationException("压缩包中的图片请解压后整理。");
        if (!await Ui.Confirm(this, "移到回收站", entry.Name, "移到回收站")) return;
        await new RecycleBin().MoveAsync(entry.Container, _lifetime.Token);
        _preferences.Bookmarks = _preferences.Bookmarks.Where(item => item.Identity != entry.Identity).ToArray(); SavePreferences();
        _entries = _entries.Where(item => item.Identity != entry.Identity).ToArray(); RenderFiles();
        if (_entries.Length > 0) await LoadAsync(Math.Min(_index, _entries.Length - 1));
        else { _index = -1; ReleaseImages(); _empty.Text = Localization.Text("没有可显示的图片"); _empty.IsVisible = true; _status.Text = ""; }
    }
    private async Task RevealAsync()
    {
        if (CurrentEntry is not { } entry) return;
        if (OperatingSystem.IsMacOS()) await ProcessRunner.Run("/usr/bin/open", ["-R", entry.Container], _lifetime.Token);
        else if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(entry.Container); Process.Start(start);
        }
        else OpenExternal(Path.GetDirectoryName(entry.Container)!);
    }
    private async Task<string?> EditorPathAsync()
    {
        var chosen = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择图片编辑器"), AllowMultiple = false });
        return chosen.FirstOrDefault()?.TryGetLocalPath();
    }
    private async Task EditExternallyAsync()
    {
        if (CurrentEntry is not { } entry) return;
        if (entry.InArchive) throw new InvalidOperationException("请先将压缩包中的图片复制到文件夹。");
        var editor = _preferences.Editor;
        if (string.IsNullOrWhiteSpace(editor) || !File.Exists(editor) && !Directory.Exists(editor))
        { editor = await EditorPathAsync(); if (editor is null) return; _preferences.Editor = editor; SavePreferences(); }
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : editor) { UseShellExecute = false };
        if (OperatingSystem.IsMacOS()) { start.ArgumentList.Add("-a"); start.ArgumentList.Add(editor); }
        start.ArgumentList.Add(entry.Container); Process.Start(start);
    }
    private async Task PrintAsync()
    {
        if (_document is not { } document) return;
        Directory.CreateDirectory(_temporaryFolder);
        var file = Path.Combine(_temporaryFolder, Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(file, document.Frames[_frame].Png, _lifetime.Token);
        if (OperatingSystem.IsMacOS())
        {
            var script = "on run argv\ntell application \"Preview\"\nactivate\nprint (POSIX file (item 1 of argv)) with print dialog\nend tell\nend run";
            var result = await ProcessRunner.Run("/usr/bin/osascript", ["-e", script, file], _lifetime.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        }
        else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "print" });
        else OpenExternal(file);
    }
    private async Task SetWallpaperAsync()
    {
        if (CurrentEntry is not { } entry) return;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "wallpaper");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".bmp" : ".png"));
        await ImageCodec.ExportAsync(entry, file, new ImageEncodingOptions(OperatingSystem.IsWindows() ? "bmp" : "png",
            Rotation: _viewport.Rotation, Flip: _viewport.Flipped), _lifetime.Token);
        if (OperatingSystem.IsWindows()) SetWindowsWallpaper(file);
        else if (OperatingSystem.IsMacOS())
        {
            var script = "on run argv\ntell application \"System Events\"\ntell every desktop to set picture to (item 1 of argv)\nend tell\nend run";
            var result = await ProcessRunner.Run("/usr/bin/osascript", ["-e", script, file], _lifetime.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        }
        else throw new PlatformNotSupportedException("当前平台尚未接入桌面背景设置。");
    }
    [SupportedOSPlatform("windows")]
    private static void SetWindowsWallpaper(string file)
    {
        if (!SystemParametersInfo(0x0014, 0, file, 0x01 | 0x02)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, string value, uint flags);
    private async Task ShowEffectsAsync()
    {
        if (_document is null) return;
        var dialog = new Window { Title = "预览效果", Width = 360, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var effect = new ComboBox { ItemsSource = new[] { "原图", "反色", "柔化", "柔化与锐化" }, SelectedIndex = 0 };
        var gamma = new NumericUpDown { Minimum = .1m, Maximum = 5, Value = 1, Increment = .1m };
        var apply = Ui.Button("应用", () => dialog.Close(true));
        dialog.Content = new StackPanel { Spacing = 10, Margin = new(16), Children = { effect, Ui.Text("伽马", "caption"), Ui.Adjust(gamma), apply } };
        var generation = _loadGeneration; var document = _document;
        if (!await dialog.ShowDialog<bool>(this) || document != _document || generation != _loadGeneration) return;
        ToolInputs.CommitNumber(gamma);
        var frames = await ImageCodec.EffectAsync(document.Frames, effect.SelectedIndex switch { 1 => "invert", 2 => "soft", 3 => "sharp", _ => "none" }, (double)(gamma.Value ?? 1), _lifetime.Token);
        if (_closed || generation != _loadGeneration) return;
        _animation.Stop(); _viewport.SetImages(null);
        foreach (var bitmap in _bitmaps) bitmap.Dispose(); _bitmaps.Clear();
        foreach (var frame in frames) { using var stream = new MemoryStream(frame.Png); _bitmaps.Add(new Bitmap(stream)); }
        SetViewportFrame(); ScheduleFrame();
    }
    private void ToggleInfo() => _body.ColumnDefinitions[2].Width = _body.ColumnDefinitions[2].Width.Value == 0 ? new GridLength(260) : new GridLength(0);
    private void ToggleLibrary() => _body.ColumnDefinitions[0].Width = _body.ColumnDefinitions[0].Width.Value == 0 ? new GridLength(210) : new GridLength(0);
    private void ChangePage(int direction)
    {
        if (_document is not { Frames.Length: > 1 } document) return;
        _animation.Stop(); _animationPaused = true; _frame = (_frame + direction + document.Frames.Length) % document.Frames.Length;
        _animationButton.Content = Localization.Text("播放动画"); SetViewportFrame(); RefreshStatus();
    }
}
