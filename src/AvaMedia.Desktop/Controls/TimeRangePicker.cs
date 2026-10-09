using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed class TimeRangePicker : UserControl
{
    private readonly Slider _start = new(), _end = new();
    private readonly TextBlock _startLabel = Ui.Text("", "caption"), _endLabel = Ui.Text("", "caption");
    private bool _updating;
    public double Start => _start.Value;
    public double End => _end.Value;
    public event Action? Changed;

    public TimeRangePicker()
    {
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(_startLabel); root.Children.Add(_start); root.Children.Add(_endLabel); root.Children.Add(_end); Content = root;
        foreach (var input in new[] { _start, _end })
        {
            input.Minimum = 0; input.Maximum = 1; input.SmallChange = .01; input.LargeChange = 1;
            input.PropertyChanged += (_, change) =>
            {
                if (_updating || change.Property != Slider.ValueProperty) return;
                _updating = true;
                if (input == _start && Start >= End) { _start.Value=Math.Min(Start,_start.Maximum-.01); _end.Value=Math.Min(_end.Maximum,Start+.01); }
                if (input == _end && End <= Start) { _end.Value=Math.Max(.01,End); _start.Value=Math.Max(0,End-.01); }
                Labels(); _updating = false; Changed?.Invoke();
            };
        }
        Avalonia.Automation.AutomationProperties.SetName(_start, "开始时间");
        Avalonia.Automation.AutomationProperties.SetName(_end, "结束时间");
    }

    public void SetRange(double start, double end, double duration)
    {
        _updating = true; _start.Maximum = _end.Maximum = Math.Max(.01, Math.Max(duration, Math.Max(start,end)));
        _start.Value = Math.Clamp(start, 0, _start.Maximum); _end.Value = Math.Clamp(end, 0, _end.Maximum);
        Labels(); _updating = false;
    }

    private void Labels()
    {
        Localization.SetText(_startLabel, $"开始时间 · {MediaTime.Format(Start)}");
        Localization.SetText(_endLabel, $"结束时间 · {MediaTime.Format(End)}");
    }
}
