using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed class ImageCropEditor : UserControl, IDisposable
{
    private readonly IMediaEngine _engine;
    private readonly Func<ConversionEntry, ConversionOptions> _options;
    private readonly CancellationToken _lifetime;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly CropOverlay _crop = new() { PixelStep = 1 };
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
    private readonly TextBlock _dimensions = new() { Classes = { "caption" } };
    private readonly TextBlock _error = new() { Classes = { "error" }, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _ratio;
    private readonly Button _reset = new() { Content = "重置选区" };
    private readonly NumericUpDown[] _coordinates = Enumerable.Range(0, 4).Select(_ => new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Increment = 1, Value = 0, MinWidth = 80 }).ToArray();
    private readonly Grid _coordinateGrid = new() { ColumnDefinitions = new("Auto,*,Auto,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
    private ConversionEntry? _entry;
    private MediaInfo? _info;
    private CancellationTokenSource? _load;
    private bool _syncing;
    private bool _disposed;
    public event Action? Changed;
    public bool CanConfirm => _entry?.Include != true || _info is not null && string.IsNullOrEmpty(_error.Text);

    public ImageCropEditor(IMediaEngine engine, Func<ConversionEntry, ConversionOptions> options, CancellationToken lifetime)
    {
        _engine = engine; _options = options; _lifetime = lifetime;
        var root = new Grid { RowDefinitions = new("*,Auto,Auto,Auto,Auto"), RowSpacing = 8 };
        var preview = new Grid { MinHeight = 180 };
        preview.Bind(Panel.BackgroundProperty, new DynamicResourceExtension("UiMediaSurface"));
        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiMediaText"));
        preview.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _image });
        preview.Children.Add(_crop); preview.Children.Add(_status); root.Children.Add(preview);
        _ratio = Ui.Combo(["自由选区", "原画面比例", "16:9", "4:3", "1:1", "9:16"], "自由选区");
        _ratio.MinWidth = 150;
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { Ui.Text("选区比例"), _ratio, _reset })
        { control.Margin = new(0, 0, 12, 0); toolbar.Children.Add(control); }
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        Grid.SetRow(_dimensions, 2); root.Children.Add(_dimensions);
        string[] labels = ["X", "Y", "宽度", "高度"];
        for (var index = 0; index < _coordinates.Length; index++)
        {
            var label = Ui.Text(labels[index]); label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, index / 2); Grid.SetColumn(label, index % 2 * 2); _coordinateGrid.Children.Add(label);
            var field = _coordinates[index]; field.Name = "ImageCrop" + new[] { "X", "Y", "Width", "Height" }[index];
            Avalonia.Automation.AutomationProperties.SetName(field, labels[index]);
            Grid.SetRow(field, index / 2); Grid.SetColumn(field, index % 2 * 2 + 1); _coordinateGrid.Children.Add(field);
            field.ValueChanged += (_, _) => { if (!_syncing) ApplyCoordinates(); };
        }
        var exact = new Expander { Header = "精确坐标", Content = _coordinateGrid, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(exact, 3); root.Children.Add(exact);
        Grid.SetRow(_error, 4); root.Children.Add(_error); Content = root;
        _crop.Changed += ApplySelection;
        _reset.Click += (_, _) => { if (_info is not null) { _ratio.SelectedIndex = 0; ApplySelection(new(0, 0, _info.Width, _info.Height)); } };
        _ratio.SelectionChanged += (_, _) => ApplyRatio();
        SetEnabled(false); _status.Text = Localization.Text("请添加文件。");
    }

    public void Select(ConversionEntry? entry)
    {
        if (_disposed || ReferenceEquals(entry, _entry)) return;
        if (_entry is not null) _entry.PropertyChanged -= EntryChanged;
        _load?.Cancel(); _load?.Dispose(); _load = null;
        _entry = entry; _info = null;
        if (entry is not null) entry.PropertyChanged += EntryChanged;
        ClearImage(); _dimensions.Text = _error.Text = ""; SetEnabled(false);
        _status.IsVisible = true;
        _status.Text = Localization.Text(entry is null ? "请添加文件。" : "正在读取预览…");
        if (entry is null) return;
        _load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _ = LoadAsync(entry, _load.Token);
    }

    private async Task LoadAsync(ConversionEntry entry, CancellationToken token)
    {
        try
        {
            var options = _options(entry);
            var info = await _engine.Probe(entry.Path, token, options.VideoStreamIndex, options.AudioStreamIndex);
            if (!info.HasVideo || info.Width < 1 || info.Height < 1) throw new InvalidDataException("文件不包含可裁剪的图片。");
            var bytes = await _engine.Thumbnail(entry.Path, 0, 1200, 900, token, pad: false, videoStreamIndex: info.VideoStreamIndex);
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(entry, _entry)) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            _image.Width = info.Width; _image.Height = info.Height; _image.Source = bitmap;
            _info = info; _crop.SourceWidth = info.Width; _crop.SourceHeight = info.Height;
            _status.IsVisible = false; SetEnabled(true); ApplyRatio(); Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_disposed || token.IsCancellationRequested || !ReferenceEquals(entry, _entry)) return;
            Localization.SetText(_status, $"预览读取失败：{exception.Message}"); _status.IsVisible = true;
        }
    }

    private void EntryChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (!_syncing && change.PropertyName == nameof(ConversionEntry.Summary)) Refresh();
        if (change.PropertyName == nameof(ConversionEntry.Include)) Changed?.Invoke();
    }

    public void Refresh()
    {
        if (_entry is null || _info is null || _disposed) return;
        var options = _options(_entry);
        var area = options.CropWidth > 0 || options.CropHeight > 0
            ? new CropArea(options.CropX, options.CropY, options.CropWidth, options.CropHeight)
            : new CropArea(0, 0, _info.Width, _info.Height);
        SetCoordinates(area);
        try { CropGeometry.Validate(area, _info, evenPixels: false); ShowSelection(area); }
        catch (ArgumentException exception) { _crop.Selection = default; _crop.InvalidateVisual(); _error.Text = Localization.Text(exception.Message); Changed?.Invoke(); }
    }

    private void SetCoordinates(CropArea area)
    {
        _syncing = true;
        int[] values = [area.X, area.Y, area.Width, area.Height];
        for (var index = 0; index < values.Length; index++) _coordinates[index].Value = values[index];
        _syncing = false;
    }

    private void ApplyCoordinates()
    {
        if (_info is null || _entry is null) return;
        try
        {
            var area = ReadCoordinates();
            ApplySelection(new(area.X, area.Y, area.Width, area.Height));
        }
        catch (ArgumentException exception) { _error.Text = Localization.Text(exception.Message); Changed?.Invoke(); }
    }

    private CropArea ReadCoordinates(bool fromText = false)
    {
        var values = new int[4];
        for (var index = 0; index < values.Length; index++)
        {
            var field = _coordinates[index];
            var value = fromText ? decimal.TryParse(field.Text, out var parsed) ? (decimal?)parsed : null : field.Value;
            if (value is not { } number || number != decimal.Truncate(number) || number < 0 || number > int.MaxValue)
                throw new ArgumentException("裁剪区域请输入整数像素。");
            values[index] = (int)number;
        }
        var area = new CropArea(values[0], values[1], values[2], values[3]);
        CropGeometry.Validate(area, _info!, evenPixels: false);
        return area;
    }

    public void ValidateSelection()
    {
        if (_entry?.Include != true) return;
        if (_info is null) throw new ArgumentException("正在读取预览…");
        var area = ReadCoordinates(fromText: true);
        ApplySelection(new(area.X, area.Y, area.Width, area.Height));
    }

    private void ApplySelection(Rect rect)
    {
        if (_entry is null || _info is null) return;
        var area = new CropArea((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
        try { CropGeometry.Validate(area, _info, evenPixels: false); }
        catch (ArgumentException exception) { _error.Text = Localization.Text(exception.Message); Changed?.Invoke(); return; }
        var options = _options(_entry).Clone();
        var full = area.X == 0 && area.Y == 0 && area.Width == _info.Width && area.Height == _info.Height;
        options.CropX = area.X; options.CropY = area.Y;
        options.CropWidth = full ? 0 : area.Width; options.CropHeight = full ? 0 : area.Height;
        options.VideoStreamIndex = _info.VideoStreamIndex;
        _syncing = true; _entry.SetOptions(options); _syncing = false;
        SetCoordinates(area); ShowSelection(area);
    }

    private void ShowSelection(CropArea area)
    {
        _crop.Selection = new(area.X, area.Y, area.Width, area.Height); _crop.InvalidateVisual(); _error.Text = "";
        Localization.SetText(_dimensions, $"{_info!.Width} × {_info.Height} · 裁剪 {area.X},{area.Y} {area.Width} × {area.Height}");
        Changed?.Invoke();
    }

    private void ApplyRatio()
    {
        _crop.AspectRatio = _ratio.SelectedIndex switch { 1 => _info is { Height: > 0 } ? (double)_info.Width / _info.Height : 0, 2 => 16d / 9, 3 => 4d / 3, 4 => 1, 5 => 9d / 16, _ => 0 };
    }

    private void SetEnabled(bool enabled)
    {
        _crop.Enabled = enabled; _crop.InvalidateVisual(); _reset.IsEnabled = _ratio.IsEnabled = _coordinateGrid.IsEnabled = enabled;
        Changed?.Invoke();
    }

    private void ClearImage()
    {
        var bitmap = _image.Source as Bitmap; _image.Source = null; bitmap?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _load?.Cancel(); _load?.Dispose();
        if (_entry is not null) _entry.PropertyChanged -= EntryChanged;
        ClearImage();
    }
}
