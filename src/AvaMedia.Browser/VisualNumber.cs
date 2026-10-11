using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AvaMedia.Browser;

/// <summary>Slider plus step buttons; values are displayed, never typed.</summary>
internal sealed class VisualNumber : UserControl
{
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _value = new() { MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? ValueChanged;
    protected override Type StyleKeyOverride => typeof(UserControl);
    public decimal Minimum { get => (decimal)_slider.Minimum; set => _slider.Minimum = (double)value; }
    public decimal Maximum { get => (decimal)_slider.Maximum; set => _slider.Maximum = (double)value; }
    public decimal Increment { get => (decimal)_slider.TickFrequency; set => _slider.TickFrequency = (double)value; }
    public decimal? Value { get => (decimal)_slider.Value; set => _slider.Value = (double)(value ?? 0); }
    public VisualNumber()
    {
        var minus = new Button { Content = "-" }; var plus = new Button { Content = "+" };
        minus.Click += (_, _) => Value = Math.Max(Minimum, (Value ?? 0) - Increment);
        plus.Click += (_, _) => Value = Math.Min(Maximum, (Value ?? 0) + Increment);
        var row = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 5 };
        row.Children.Add(minus); Grid.SetColumn(_slider, 1); row.Children.Add(_slider);
        Grid.SetColumn(plus, 2); row.Children.Add(plus); Grid.SetColumn(_value, 3); row.Children.Add(_value); Content = row;
        _slider.PropertyChanged += (_, args) =>
        {
            if (args.Property != Slider.ValueProperty) return;
            _value.Text = _slider.Value.ToString("0.###"); ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _value.Text = "0";
    }
}
