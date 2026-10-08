using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed partial class SubtitleStyleEditor : UserControl, IDisposable
{
    private readonly ComboBox _font;
    private readonly NumericUpDown _size;
    private readonly NumericUpDown _margin;
    private readonly TextBox _color;
    private readonly ToggleButton[] _positions = new ToggleButton[9];
    private readonly Image _frame = new() { Name = "SubtitlePreviewFrame", Stretch = Stretch.Fill };
    private readonly TextBlock _sample = new() { Text = "字幕预览", TextWrapping = TextWrapping.Wrap };
    private readonly Border _caption;
    private readonly Canvas _captionLayer = new();
    private readonly Grid _screen = new() { Width = 320, Height = 180, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid _previewHost = new() { MaxHeight = 300 };
    private double _previewAspect = 16d / 9;
    private Bitmap? _bitmap;
    private double _referenceHeight = 1080;
    private readonly double _defaultSize;
    private int _alignment;

    public SubtitleStyleEditor(ConversionOptions options, int referenceHeight = 1080)
    {
        _referenceHeight = referenceHeight; _defaultSize = referenceHeight == 288 ? 16 : 48;
        _alignment = Math.Clamp(options.SubtitleAlignment, 1, 9);
        if (options.SubtitlePositionX is {} x && options.SubtitlePositionY is {} y) _customPosition = new(x, y);
        var root = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 20 };
        var fields = new StackPanel { Spacing = 12 }; root.Children.Add(fields);
        var automatic = Localization.Text("自动");
        var fonts = FontManager.Current.SystemFonts.Select(font => font.Name).Append(options.SubtitleFont).Where(name => name.Length > 0).Distinct().OrderBy(name => name);
        _font = Ui.Combo(new[] { automatic }.Concat(fonts), options.SubtitleFont.Length > 0 ? options.SubtitleFont : automatic); _font.Name = "SubtitleFont";
        Localization.SetIsUserText(_font, true);
        Add(fields, "字体", _font);
        _size = Number("SubtitleFontSize", options.SubtitleFontSize, 0, 200);
        ToolTip.SetTip(_size, "0 = 自动"); Add(fields, "字号", _size);
        _color = Ui.Input(options.SubtitleColor); _color.Name = "SubtitleColor";
        var colors = new StackPanel { Spacing = 8 }; colors.Children.Add(_color);
        var palette = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in new[] { "#FFFFFF", "#FFFF00", "#FFD166", "#00E5FF", "#FF6B6B", "#76FF03", "#C792EA", "#000000" })
        {
            var swatch = new Button { Width = 30, MinWidth = 30, Padding = new(4), Margin = new(0, 0, 4, 0),
                Content = new Border { Width = 18, Height = 18, Background = SolidColorBrush.Parse(hex) } };
            AutomationProperties.SetName(swatch, hex); ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => _color.Text = hex; palette.Children.Add(swatch);
        }
        colors.Children.Add(palette); Add(fields, "颜色", colors);
        var positions = new Grid { ColumnDefinitions = new("Auto,Auto,Auto"), RowDefinitions = new("Auto,Auto,Auto") };
        string[] arrows = ["↙", "↓", "↘", "←", "●", "→", "↖", "↑", "↗"];
        string[] names = ["左下", "中下", "右下", "左中", "居中", "右中", "左上", "中上", "右上"];
        for (var index = 0; index < 9; index++)
        {
            var alignment = index + 1;
            var button = new ToggleButton { Name = "SubtitlePosition" + alignment, Content = arrows[index],
                MinWidth = 40, Margin = new(0, 0, 4, 4), IsChecked = _customPosition is null && alignment == _alignment };
            AutomationProperties.SetName(button, names[index]); ToolTip.SetTip(button, names[index]);
            button.Click += (_, _) => { _customPosition = null; _alignment = alignment; foreach (var item in _positions) item.IsChecked = item == button; UpdatePreview(); };
            Grid.SetRow(button, 2 - index / 3); Grid.SetColumn(button, index % 3);
            positions.Children.Add(button); _positions[index] = button;
        }
        Add(fields, "位置", positions);
        _margin = Number("SubtitleMargin", options.SubtitleMargin, 0, 2000); Add(fields, "边距", _margin);
        _screen.Background = new SolidColorBrush(Color.Parse("#202428")); _screen.Children.Add(_frame);
        _caption = new Border { Name = "SubtitlePreviewCaption", Child = _sample, Padding = new(4, 2), Background = new SolidColorBrush(Color.Parse("#66000000")) };
        _captionLayer.Children.Add(_caption); _screen.Children.Add(_captionLayer);
        var preview = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        preview.Children.Add(Ui.Text("预览", "caption"));
        _previewHost.Children.Add(_screen); _previewHost.SizeChanged += (_, _) => FitPreview(); preview.Children.Add(_previewHost);
        SetupPreview(preview, referenceHeight, options.Start);
        Grid.SetColumn(preview, 1); root.Children.Add(preview); Content = root;
        _font.SelectionChanged += (_, _) => UpdatePreview(); _color.TextChanged += (_, _) => UpdatePreview();
        _size.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty) UpdatePreview(); };
        _margin.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty) UpdatePreview(); };
        UpdatePreview();
    }

    public void ReadInto(ConversionOptions options)
    {
        options.SubtitleFont = _font.SelectedIndex <= 0 ? "" : (string)_font.SelectedItem!;
        options.SubtitleFontSize = ReadNumber(_size); options.SubtitleMargin = ReadNumber(_margin);
        options.SubtitleColor = _color.Text?.Trim().ToUpperInvariant() ?? "";
        options.SubtitleAlignment = _alignment;
        options.SubtitlePositionX = _customPosition?.X; options.SubtitlePositionY = _customPosition?.Y;
        var validation = options.Clone(); validation.SubtitleMode = SubtitleMode.None;
        SubtitleOptions.Validate(validation);
    }

    public void SetFrame(byte[] image, int width, int height, bool sourceResolution = true)
    {
        using var input = new MemoryStream(image); var next = new Bitmap(input);
        _frame.Source = next; _bitmap?.Dispose(); _bitmap = next;
        _previewAspect = (double)Math.Max(1, width) / Math.Max(1, height); FitPreview();
        _referenceHeight = sourceResolution ? Math.Max(1, height) : 288;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _frame.Width = _screen.Width; _frame.Height = _screen.Height;
        _captionLayer.Width = _screen.Width; _captionLayer.Height = _screen.Height;
        var scale = _screen.Height / _referenceHeight;
        _sample.FontSize = ((double)(_size.Value ?? 0) is > 0 and var fontSize ? fontSize : _defaultSize) * scale;
        _sample.FontFamily = _font.SelectedIndex <= 0 ? FontFamily.Default : new FontFamily((string)_font.SelectedItem!);
        if (Color.TryParse(_color.Text, out var color)) _sample.Foreground = new SolidColorBrush(color);
        var margin = _customPosition is null ? Math.Min((double)(_margin.Value ?? 0) * scale, Math.Min(_screen.Width, _screen.Height) / 3) : 0;
        _sample.MaxWidth = Math.Max(1, _screen.Width - margin * 2 - _caption.Padding.Left - _caption.Padding.Right);
        _sample.InvalidateMeasure(); _caption.InvalidateMeasure();
        _caption.Width = _caption.Height = double.NaN;
        _caption.Measure(new Size(_screen.Width - margin * 2, double.PositiveInfinity));
        var size = _caption.DesiredSize; _caption.Width = size.Width; _caption.Height = size.Height;
        _margin.IsEnabled = _customPosition is null;
        double left, top;
        if (_customPosition is {} position)
        {
            left = position.X * _screen.Width - size.Width / 2; top = position.Y * _screen.Height - size.Height / 2;
        }
        else
        {
            left = ((_alignment - 1) % 3) switch { 0 => margin, 1 => (_screen.Width - size.Width) / 2, _ => _screen.Width - margin - size.Width };
            top = ((_alignment - 1) / 3) switch { 0 => _screen.Height - margin - size.Height, 1 => (_screen.Height - size.Height) / 2, _ => margin };
        }
        Canvas.SetLeft(_caption, left); Canvas.SetTop(_caption, top);
        _captionLayer.InvalidateArrange();
    }

    private void FitPreview()
    {
        var width = Math.Min(_previewHost.Bounds.Width > 0 ? _previewHost.Bounds.Width : 320, 300 * _previewAspect);
        if (Math.Abs(_screen.Width - width) < .01 && Math.Abs(_screen.Height - width / _previewAspect) < .01) return;
        _screen.Width = width; _screen.Height = width / _previewAspect; UpdatePreview();
    }

    private static NumericUpDown Number(string name, int value, int minimum, int maximum) => new()
    { Name = name, Minimum = minimum, Maximum = maximum, Value = value, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private static int ReadNumber(NumericUpDown control)
    {
        var value = control.Value ?? 0;
        if (!string.IsNullOrEmpty(control.Text) && !decimal.TryParse(control.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
            || value < control.Minimum || value > control.Maximum || value != decimal.Truncate(value)) throw new ArgumentException("字幕字号和边距必须是范围内的整数。");
        return (int)value;
    }
    private static void Add(Panel fields, string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new("48,*"), ColumnSpacing = 8 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); fields.Children.Add(row);
    }
    public void Dispose() { _disposed = true; _videoSource?.Cancel(); _seekFrame?.Cancel(); _videoSource?.Dispose(); _seekFrame?.Dispose(); _frame.Source = null; _bitmap?.Dispose(); _bitmap = null; }
}

public sealed class VoiceEnhancementControl : UserControl
{
    public CheckBox EnabledInput { get; }
    public Slider StrengthInput { get; }
    public VoiceEnhancementControl(ConversionOptions options, bool required = false)
    {
        EnabledInput = new CheckBox { Name = "VoiceEnhancement", Content = "人声增强", IsChecked = required || options.VoiceEnhancement, IsVisible = !required };
        StrengthInput = new Slider { Name = "VoiceStrength", Minimum = 1, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = options.VoiceEnhancementStrength };
        var value = Ui.Text("", "caption"); var row = new Grid { ColumnDefinitions = new("80,*,42"), ColumnSpacing = 8 };
        row.Children.Add(Ui.Text("降噪强度")); Grid.SetColumn(StrengthInput, 1); row.Children.Add(StrengthInput); Grid.SetColumn(value, 2); row.Children.Add(value);
        var root = new StackPanel { Spacing = 8 }; root.Children.Add(EnabledInput); root.Children.Add(row); Content = root;
        void Refresh() { row.IsVisible = EnabledInput.IsChecked == true; value.Text = ((int)StrengthInput.Value).ToString(CultureInfo.InvariantCulture) + "%"; }
        EnabledInput.IsCheckedChanged += (_, _) => Refresh();
        StrengthInput.PropertyChanged += (_, args) => { if (args.Property == Slider.ValueProperty) Refresh(); }; Refresh();
    }
    public void ReadInto(ConversionOptions options)
    { options.VoiceEnhancement = EnabledInput.IsChecked == true; options.VoiceEnhancementStrength = (int)Math.Round(StrengthInput.Value); }
}
