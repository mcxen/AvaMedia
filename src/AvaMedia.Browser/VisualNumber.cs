using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.Browser;

/// <summary>Compact stepper: values are displayed, never typed. Drag the value horizontally to scrub.</summary>
internal sealed class VisualNumber : UserControl
{
    private decimal _minimum, _maximum = 100, _increment = 1, _value;
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12.5 };
    private readonly Border _box;
    private Point? _drag; private decimal _dragStart;
    public event EventHandler? ValueChanged;
    public string Suffix { get; set; } = "";
    public Func<decimal, string>? Formatter { get; set; }
    protected override Type StyleKeyOverride => typeof(UserControl);
    public decimal Minimum { get => _minimum; set { _minimum = value; Set(_value, false); } }
    public decimal Maximum { get => _maximum; set { _maximum = value; Set(_value, false); } }
    public decimal Increment { get => _increment; set => _increment = value <= 0 ? 1 : value; }
    public decimal? Value { get => _value; set => Set(value ?? 0, true); }

    public VisualNumber()
    {
        var minus = Step("minus", -1); var plus = Step("plus", 1);
        _box = new Border { Child = _text, MinWidth = 64, Padding = new(8, 3), CornerRadius = new(7), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E5")), BorderThickness = new(1), Cursor = new Cursor(StandardCursorType.SizeWestEast) };
        _box.PointerPressed += (_, e) => { _drag = e.GetPosition(this); _dragStart = _value; e.Pointer.Capture(_box); e.Handled = true; };
        _box.PointerMoved += (_, e) =>
        {
            if (_drag is not { } start) return;
            var steps = (decimal)Math.Round((e.GetPosition(this).X - start.X) / 4);
            var range = _maximum - _minimum; var increment = range / _increment > 2000 ? range / 500 : _increment;
            Set(_dragStart + steps * increment, true);
        };
        _box.PointerReleased += (_, e) => { _drag = null; e.Pointer.Capture(null); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(minus); row.Children.Add(_box); row.Children.Add(plus); Content = row;
        Render();
    }
    private Button Step(string icon, int direction)
    {
        var button = new Button { Content = Ui.Icon(icon, 12, Palette.Secondary), Width = 24, Height = 24, MinHeight = 0, Padding = new(0) };
        button.Classes.Add("plain");
        button.Click += (_, _) => Set(_value + direction * _increment, true);
        return button;
    }
    private void Set(decimal value, bool notify)
    {
        var next = Math.Clamp(value, _minimum, Math.Max(_minimum, _maximum));
        if (_increment > 0 && _increment < 1) next = Math.Round(next / _increment) * _increment;
        else if (_increment >= 1) next = Math.Round(next);
        var changed = next != _value; _value = next; Render();
        if (changed && notify) ValueChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Render() => _text.Text = Formatter?.Invoke(_value) ?? _value.ToString("0.###") + Suffix;
}
