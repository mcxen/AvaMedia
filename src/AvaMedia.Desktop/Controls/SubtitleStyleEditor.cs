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

public sealed class SubtitleStyleEditor : UserControl, IDisposable
{
    private readonly ComboBox _font;
    private readonly NumericUpDown _size;
    private readonly NumericUpDown _margin;
    private readonly TextBox _color;
    private readonly ToggleButton[] _positions = new ToggleButton[9];
    private readonly Image _frame = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _sample = new() { Text = "自动字幕 · Subtitle preview", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Border _caption;
    private readonly Grid _screen = new() { Width = 640, Height = 360, ClipToBounds = true };
    private Bitmap? _bitmap;
    private double _referenceHeight = 1080;
    private readonly double _defaultSize;
    private int _alignment;

    public SubtitleStyleEditor(ConversionOptions options, int referenceHeight = 1080)
    {
        _referenceHeight = referenceHeight; _defaultSize = referenceHeight == 288 ? 16 : 48;
        _alignment = Math.Clamp(options.SubtitleAlignment, 1, 9);
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
                MinWidth = 40, Margin = new(0, 0, 4, 4), IsChecked = alignment == _alignment };
            AutomationProperties.SetName(button, names[index]); ToolTip.SetTip(button, names[index]);
            button.Click += (_, _) => { _alignment = alignment; foreach (var item in _positions) item.IsChecked = item == button; UpdatePreview(); };
            Grid.SetRow(button, 2 - index / 3); Grid.SetColumn(button, index % 3);
            positions.Children.Add(button); _positions[index] = button;
        }
        Add(fields, "位置", positions);
        _margin = Number("SubtitleMargin", options.SubtitleMargin, 0, 2000); Add(fields, "边距", _margin);
        _screen.Background = new SolidColorBrush(Color.Parse("#202428")); _screen.Children.Add(_frame);
        _caption = new Border { Child = _sample, Padding = new(4, 2), Background = new SolidColorBrush(Color.Parse("#66000000")) };
        _screen.Children.Add(_caption);
        var preview = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        preview.Children.Add(Ui.Text("预览", "caption"));
        preview.Children.Add(new Viewbox { Child = _screen, Stretch = Stretch.Uniform, MaxHeight = 300, HorizontalAlignment = HorizontalAlignment.Stretch });
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
        var validation = options.Clone(); validation.SubtitleMode = SubtitleMode.None;
        SubtitleOptions.Validate(validation);
    }

    public void SetFrame(byte[] image, int width, int height, bool sourceResolution = true)
    {
        using var input = new MemoryStream(image); var next = new Bitmap(input);
        _frame.Source = next; _bitmap?.Dispose(); _bitmap = next;
        _screen.Height = 640d * Math.Max(1, height) / Math.Max(1, width);
        _referenceHeight = sourceResolution ? Math.Max(1, height) : 288;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var scale = _screen.Height / _referenceHeight;
        _sample.FontSize = ((double)(_size.Value ?? 0) is > 0 and var size ? size : _defaultSize) * scale;
        _sample.FontFamily = _font.SelectedIndex <= 0 ? FontFamily.Default : new FontFamily((string)_font.SelectedItem!);
        if (Color.TryParse(_color.Text, out var color)) _sample.Foreground = new SolidColorBrush(color);
        _caption.HorizontalAlignment = ((_alignment - 1) % 3) switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right };
        _caption.VerticalAlignment = ((_alignment - 1) / 3) switch { 0 => VerticalAlignment.Bottom, 1 => VerticalAlignment.Center, _ => VerticalAlignment.Top };
        _caption.Margin = new(Math.Min((double)(_margin.Value ?? 0) * scale, _screen.Height / 3));
        _sample.MaxWidth = Math.Max(1, _screen.Width - _caption.Margin.Left * 2);
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
    public void Dispose() { _frame.Source = null; _bitmap?.Dispose(); _bitmap = null; }
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
