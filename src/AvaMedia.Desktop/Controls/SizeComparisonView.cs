using Avalonia.Controls;
using Avalonia.Layout;

namespace AvaMedia.Desktop.Controls;

public sealed class SizeComparisonView : UserControl
{
    private readonly ProgressBar _source = new() { Height = 12 };
    private readonly ProgressBar _target = new() { Height = 12 };
    private readonly TextBlock _sourceLabel = Ui.Text("", "caption");
    private readonly TextBlock _targetLabel = Ui.Text("", "caption");

    public SizeComparisonView()
    {
        var root = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(_sourceLabel); root.Children.Add(_source);
        root.Children.Add(_targetLabel); root.Children.Add(_target); Content = root;
        IsVisible = false;
    }

    public void SetSizes(long source, long? target)
    {
        IsVisible = source > 0;
        var maximum = Math.Max(1, Math.Max(source, target ?? source));
        _source.Maximum = _target.Maximum = maximum;
        _source.Value = source; _target.Value = target ?? 0;
        _target.IsVisible = target is not null;
        Localization.SetText(_sourceLabel, $"原体积 {source / 1000000d:0.##} MB");
        _targetLabel.Text = target is { } bytes
            ? Localization.Format($"预计 {bytes / 1000000d:0.##} MB · 节省 {(source - bytes) * 100d / Math.Max(1, source):0.#}%")
            : Localization.Text("输出体积由内容决定");
    }
}
