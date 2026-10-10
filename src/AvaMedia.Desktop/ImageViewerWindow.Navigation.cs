using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class ImageViewerWindow
{
    private async Task<byte[]> ReadLibraryThumbnailAsync(ImageViewerEntry entry, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        return await ImageCodec.ThumbnailAsync(entry, 96, 88, false, linked.Token);
    }
    private async Task PickAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Localization.Text("打开图片或压缩包"), AllowMultiple = true,
            FileTypeFilter = [new("图片与压缩包") { Patterns = ImageFormats.Extensions.Select(extension => "*." + extension)
                .Concat(new[] { "*.zip", "*.rar", "*.7z", "*.tar", "*.lzh", "*.lha", "*.cbr", "*.cbz", "*.cb7", "*.cbt" }).ToArray() },
                Avalonia.Platform.Storage.FilePickerFileTypes.All]
        });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await OpenAsync(paths);
    }
    private async Task OpenAsync(IEnumerable<string> inputs, string? selected = null)
    {
        var paths = inputs.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath).ToArray();
        if (paths.Length == 0) return;
        var generation = ++_openGeneration; _opening?.Cancel(); _loading?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _opening = operation;
        _status.Text = Localization.Text("读取图片列表…");
        try
        {
            var sources = paths;
            if (paths.Length == 1 && File.Exists(paths[0]) && !ImageViewerSource.IsArchive(paths[0]))
            { selected ??= paths[0]; sources = [Path.GetDirectoryName(paths[0])!]; }
            var entries = await ImageViewerSource.ListAsync(sources, _recursive.IsChecked == true, operation.Token);
            if (_closed || generation != _openGeneration) return;
            _sources = paths; _entries = entries; _index = -1; _showBookmarks = false;
            _preferences.Recent = paths.Concat(_preferences.Recent).Distinct(BatchRename.PathComparer).Take(12).ToArray(); SavePreferences();
            RenderFiles();
            var index = selected is null ? 0 : Array.FindIndex(entries,
                entry => BatchRename.PathComparer.Equals(entry.Container, selected) || entry.Member == selected);
            if (entries.Length > 0) await LoadAsync(Math.Max(0, index));
            else { ReleaseImages(); _empty.Text = Localization.Text("没有可显示的图片"); _empty.IsVisible = true; _status.Text = ""; }
        }
        finally { if (ReferenceEquals(_opening, operation)) _opening = null; }
    }
    private async Task LoadAsync(int index)
    {
        if (_closed || index < 0 || index >= _entries.Length) return;
        _loading?.Cancel(); var generation = ++_loadGeneration;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _loading = operation;
        _index = index; _animation.Stop(); ReleaseImages();
        _empty.Text = Localization.Text("加载图片…"); _empty.IsVisible = true;
        _bindingFiles = true; _files.SelectedItem = _entries[index]; _bindingFiles = false;
        var entry = _entries[index]; _status.Text = entry.Name;
        try
        {
            var document = await ImageCodec.DecodeAsync(entry, operation.Token);
            ImageViewerDocument? second = null;
            if (_spread.IsChecked == true && index + 1 < _entries.Length)
                second = await ImageCodec.DecodeAsync(_entries[index + 1], operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _loadGeneration) return;
            _document = document; _secondDocument = second; _loadedModified = File.GetLastWriteTimeUtc(entry.Container);
            foreach (var frame in document.Frames)
            { using var stream = new MemoryStream(frame.Png); _bitmaps.Add(new Bitmap(stream)); }
            if (second is not null) { using var stream = new MemoryStream(second.Frames[0].Png); _secondBitmap = new Bitmap(stream); }
            _frame = _loops = 0; _animationPaused = false; SetViewportFrame(reset: !_preferences.LockZoom);
            _empty.IsVisible = false; Title = entry.Name + " — " + Localization.Text("天池看图");
            _animationButton.IsVisible = document.Animated && document.Frames.Length > 1 && second is null;
            _animationButton.Content = Localization.Text("暂停动画");
            RenderMetadata(entry, document); RefreshBookmark(); RefreshStatus(); ScheduleFrame();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_closed || generation != _loadGeneration) return;
            ReleaseImages(); StopSlideshow(); _empty.IsVisible = true;
            _empty.Text = Localization.Text("无法打开图片"); _status.Text = entry.Name + " · " + error.Message;
        }
        finally { if (ReferenceEquals(_loading, operation)) _loading = null; }
    }
    private void ReleaseImages()
    {
        _viewport.SetImages(null); foreach (var bitmap in _bitmaps) bitmap.Dispose(); _bitmaps.Clear();
        _secondBitmap?.Dispose(); _secondBitmap = null; _document = _secondDocument = null; _metadata.Children.Clear();
        _animationButton.IsVisible = false;
    }
    private void RenderFiles()
    {
        if (_closed) return;
        _bindingFiles = true;
        try
        {
            var query = _search.Text?.Trim() ?? "";
            var entries = _showBookmarks ? _preferences.Bookmarks : _entries;
            _files.ItemsSource = entries.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            _files.SelectedItem = _index >= 0 && _index < _entries.Length ? _entries[_index] : null;
        }
        finally { _bindingFiles = false; }
    }
    private void Navigate(int direction, bool slideshow = false)
    {
        if (_entries.Length == 0 || slideshow && _loading is not null) return;
        var next = _index + direction * (_spread.IsChecked == true ? 2 : 1);
        if (slideshow && _preferences.Random && _entries.Length > 1)
        { next = Random.Shared.Next(_entries.Length - 1); if (next >= _index) next++; }
        if (next < 0 || next >= _entries.Length)
        {
            if (slideshow && !_preferences.Repeat) { StopSlideshow(); return; }
            next = direction > 0 ? 0 : _entries.Length - 1;
        }
        _ = LoadAsync(next);
    }
    private void SetViewportFrame(bool reset = false)
    {
        if (_bitmaps.Count == 0) return;
        if (_secondBitmap is not null && _preferences.RightToLeft) _viewport.SetImages(_secondBitmap, _bitmaps[_frame], reset);
        else _viewport.SetImages(_bitmaps[_frame], _secondBitmap, reset);
    }
    private void ScheduleFrame()
    {
        if (_closed || _animationPaused || _document is not { Animated: true, Frames.Length: > 1 } document || _secondDocument is not null) return;
        _animation.Interval = document.Frames[_frame].Delay; _animation.Start();
    }
    private void AdvanceFrame()
    {
        _animation.Stop();
        if (_document is not { } document || _bitmaps.Count == 0) return;
        _frame++;
        if (_frame == _bitmaps.Count)
        {
            _loops++; _frame = 0;
            if (document.LoopCount > 0 && _loops >= document.LoopCount)
            { _frame = _bitmaps.Count - 1; _animationPaused = true; _animationButton.Content = Localization.Text("播放动画"); return; }
        }
        SetViewportFrame(); RefreshStatus(); ScheduleFrame();
    }
    private void ToggleSlideshow()
    {
        if (_slideshow.IsEnabled) { StopSlideshow(); return; }
        if (_entries.Length == 0) return;
        SetSlideshowInterval(); _slideshow.Start(); _slideshowButton.Content = Localization.Text("停止幻灯片");
    }
    private void StopSlideshow() { _slideshow.Stop(); _slideshowButton.Content = Localization.Text("幻灯片"); }
    private void SetSlideshowInterval() => _slideshow.Interval = TimeSpan.FromSeconds(_interval.SelectedIndex switch { 0 => 2, 2 => 10, 3 => 30, _ => 5 });
    private void RefreshStatus()
    {
        if (_document is not { } document || _index < 0) return;
        _status.Text = $"{_index + 1} / {_entries.Length} · {_entries[_index].Name} · {_bitmaps[_frame].PixelSize.Width} × {_bitmaps[_frame].PixelSize.Height} · {document.Format} · {_viewport.Zoom:P0}"
            + (document.Frames.Length > 1 ? $" · {_frame + 1}/{document.Frames.Length}" : "");
        Localization.SetIsUserText(_status, true);
    }
    private void RenderMetadata(ImageViewerEntry entry, ImageViewerDocument document)
    {
        _metadata.Children.Clear();
        AddInfo(Localization.Text("文件"), entry.Name);
        AddInfo(Localization.Text("位置"), entry.InArchive ? entry.Container + "\n" + entry.Member : entry.Container);
        AddInfo(Localization.Text("尺寸"), $"{document.Width} × {document.Height}");
        AddInfo(Localization.Text("格式"), document.Format); AddInfo(Localization.Text("文件大小"), ImageCompression.Bytes(entry.Bytes));
        if (document.MapUrl is not null) _metadata.Children.Add(_mapButton);
        foreach (var (name, value) in document.Metadata) AddInfo(name, value);
    }
    private void AddInfo(string label, string value)
    {
        var name = Ui.Text(label, "settingsHeading"); Localization.SetIsUserText(name, true); _metadata.Children.Add(name);
        var text = Ui.Text(value); Localization.SetIsUserText(text, true); text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; _metadata.Children.Add(text);
    }
    private void ToggleBookmark()
    {
        if (_index < 0 || _index >= _entries.Length) return;
        var entry = _entries[_index]; var exists = _preferences.Bookmarks.Any(item => item.Identity == entry.Identity);
        _preferences.Bookmarks = exists ? _preferences.Bookmarks.Where(item => item.Identity != entry.Identity).ToArray()
            : _preferences.Bookmarks.Append(entry).ToArray();
        SavePreferences(); RefreshBookmark(); if (_showBookmarks) RenderFiles();
    }
    private void RefreshBookmark() => _bookmarkButton.Content = Localization.Text(_index >= 0
        && _preferences.Bookmarks.Any(item => item.Identity == _entries[_index].Identity) ? "取消收藏" : "收藏");
    private async Task ShowRecentAsync()
    {
        var window = new Window { Title = "最近打开", Width = 600, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = _preferences.Recent.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray(), Margin = new(12) };
        Localization.SetIsUserText(list, true); window.Content = list;
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is string path) window.Close(path); };
        if (await window.ShowDialog<string?>(this) is { } chosen) await OpenAsync([chosen]);
    }
    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen) { WindowState = _beforeFullscreen; _toolbar.IsVisible = _navigation.IsVisible = true; _body.ColumnDefinitions[0].Width = _beforeLibrary; _body.ColumnDefinitions[2].Width = _beforeInfo; }
        else { _beforeFullscreen = WindowState; _beforeLibrary = _body.ColumnDefinitions[0].Width; _beforeInfo = _body.ColumnDefinitions[2].Width; _body.ColumnDefinitions[0].Width = _body.ColumnDefinitions[2].Width = new GridLength(0); WindowState = WindowState.FullScreen; _toolbar.IsVisible = _navigation.IsVisible = false; }
    }
    private void HandleKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox or NumericUpDown or ComboBox) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.PageDown or Key.PageUp)
        { ChangePage(e.Key == Key.PageDown ? 1 : -1); e.Handled = true; return; }
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (e.Key == Key.F2 || command && e.Key == Key.O) { _ = GuardAsync(PickAsync); e.Handled = true; return; }
        if (command && e.Key == Key.C) { _ = GuardAsync(() => CopyImageAsync(e.KeyModifiers.HasFlag(KeyModifiers.Alt))); e.Handled = true; return; }
        if (command && e.Key == Key.P) { _ = GuardAsync(PrintAsync); e.Handled = true; return; }
        if (command && e.Key == Key.E) { _ = GuardAsync(EditExternallyAsync); e.Handled = true; return; }
        if (command && e.Key == Key.T) { _ = ExportAsync(); e.Handled = true; return; }
        if (command && e.Key == Key.D0) { StopSlideshow(); e.Handled = true; return; }
        if (command && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            if (_entries.Length > 0) { _slideshow.Interval = TimeSpan.FromSeconds((int)e.Key - (int)Key.D0); _slideshow.Start(); _slideshowButton.Content = Localization.Text("停止幻灯片"); }
            e.Handled = true; return;
        }
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Tab) { ToggleInfo(); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Right: case Key.PageDown: Navigate(1); break;
            case Key.Left: case Key.PageUp: Navigate(-1); break;
            case Key.Home: if (_entries.Length > 0) _ = LoadAsync(0); break;
            case Key.End: if (_entries.Length > 0) _ = LoadAsync(_entries.Length - 1); break;
            case Key.Add: case Key.OemPlus: _viewport.ZoomBy(1.2); break;
            case Key.Subtract: case Key.OemMinus: _viewport.ZoomBy(1 / 1.2); break;
            case Key.D0: _viewport.Fit("actual"); break;
            case Key.D1: case Key.D9: _viewport.Fit("fit"); break;
            case Key.D8: _viewport.Fit("width"); break;
            case Key.Delete: _ = GuardAsync(RecycleAsync); break;
            case Key.R: _viewport.Rotate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -90 : 90); break;
            case Key.F: _ = GuardAsync(async () => { if (await Ui.Folder(this, "打开图片文件夹") is { } folder) await OpenAsync([folder]); }); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape: if (WindowState == WindowState.FullScreen) ToggleFullscreen(); else StopSlideshow(); break;
            case Key.Space: Navigate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); break;
            case Key.B: ToggleBookmark(); break;
            default: return;
        }
        e.Handled = true;
    }
    private void ShowPlaybackSettings()
    {
        var random = new CheckBox { Content = "随机播放", IsChecked = _preferences.Random };
        var repeat = new CheckBox { Content = "循环播放", IsChecked = _preferences.Repeat };
        var lockZoom = new CheckBox { Content = "保持缩放比例", IsChecked = _preferences.LockZoom };
        lockZoom.IsCheckedChanged += (_, _) => { _preferences.LockZoom = lockZoom.IsChecked == true; SavePreferences(); };
        var rtl = new CheckBox { Content = "从右向左阅读", IsChecked = _preferences.RightToLeft };
        random.IsCheckedChanged += (_, _) => { _preferences.Random = random.IsChecked == true; SavePreferences(); };
        repeat.IsCheckedChanged += (_, _) => { _preferences.Repeat = repeat.IsChecked == true; SavePreferences(); };
        rtl.IsCheckedChanged += (_, _) => { _preferences.RightToLeft = rtl.IsChecked == true; SavePreferences(); SetViewportFrame(); };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { random, repeat, rtl, lockZoom } } }; flyout.ShowAt(_slideshowButton);
    }
    private Task HelpAsync() => Ui.Message(this, "看图快捷键",
        "← / →、Page Up / Down：翻图\nHome / End：首张 / 末张\n滚轮、+ / −：缩放；拖动：平移\n双击：适应 / 原始尺寸；1 / 0：适应 / 原始尺寸\nR / Shift+R：旋转；B：收藏\n空格：下一张；Shift+空格：上一张\nCtrl+1–9：幻灯片；Ctrl+0：停止；F11：全屏；Esc：退出全屏 / 停止播放\nAlt+Page Up / Down：多页图片翻页\nF：打开文件夹；F2 / Ctrl / ⌘ + O：打开图片\nCtrl / ⌘ + C：复制图片；P：打印\nCtrl / ⌘ + E：外部编辑；T：批量转换；Tab：信息");
}
