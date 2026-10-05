using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class ClipSplitWindow : Window
{
    private readonly ConversionOptions _draft;
    private readonly double _duration;
    private readonly ComboBox _mode = Ui.Combo(["等分段数", "每段时长", "指定时间点"], "等分段数");
    private readonly NumericUpDown _parts = new() { Name = "SplitParts", Minimum = 2, Maximum = ClipSplit.MaximumSegments, Increment = 1, Value = 2, FormatString = "0" };
    private readonly TextBox _seconds = new() { Name = "SplitSeconds", Text = "60" };
    private readonly TextBox _points = new() { Name = "SplitPoints", AcceptsReturn = true, MinHeight = 72, TextWrapping = TextWrapping.Wrap,
        Watermark = "例如 00:00:30.500, 00:01:10 或每行一个时间点" };
    private readonly StackPanel _partsPanel = new() { Spacing = 6 };
    private readonly StackPanel _secondsPanel = new() { Spacing = 6 };
    private readonly StackPanel _pointsPanel = new() { Spacing = 6 };
    private readonly TextBox _preview = new() { Name = "SplitPreview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _error = new() { Name = "SplitError", TextWrapping = TextWrapping.Wrap, Classes = { "error" } };
    private readonly Button _confirm;
    private IReadOnlyList<ConversionOptions>? _segments;

    public ClipSplitWindow(ConversionOptions draft, double duration)
    {
        _draft = draft.Clone(); _duration = duration;
        Title = "分割"; Width = 620; Height = 620; MinWidth = 560; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _mode.Name = "SplitMode";
        var grid = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto"), Margin = new Thickness(20), RowSpacing = 12 };
        var end = draft.End > 0 ? draft.End : duration;
        grid.Children.Add(new TextBlock { Text = $"当前源视频区间：{Time(draft.Start)} – {Time(end)}\n切点使用源视频时间；速度 {draft.Speed:0.###}× 在分段后应用。", TextWrapping = TextWrapping.Wrap });
        var modeRow = new Grid { ColumnDefinitions = new("100,*") }; modeRow.Children.Add(Ui.Text("分割方式"));
        Grid.SetColumn(_mode, 1); modeRow.Children.Add(_mode); Grid.SetRow(modeRow, 1); grid.Children.Add(modeRow);
        _partsPanel.Children.Add(Ui.Text($"段数（2–{ClipSplit.MaximumSegments}）")); _partsPanel.Children.Add(_parts);
        _secondsPanel.Children.Add(Ui.Text("每段时长（源视频时间；最后一段保留余数）")); _secondsPanel.Children.Add(_seconds);
        _pointsPanel.Children.Add(Ui.Text("时间点（在当前区间内按顺序输入，逗号或换行分隔）")); _pointsPanel.Children.Add(_points);
        var fields = new StackPanel { Spacing = 6 }; fields.Children.Add(_partsPanel); fields.Children.Add(_secondsPanel); fields.Children.Add(_pointsPanel);
        fields.Children.Add(new TextBlock { Text = "时间支持秒数、MM:SS 或 HH:MM:SS，小数秒使用小数点。", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(fields, 2); grid.Children.Add(fields);
        Grid.SetRow(_preview, 3); grid.Children.Add(_preview);
        var note = new StackPanel { Spacing = 6 }; note.Children.Add(_error);
        note.Children.Add(new TextBlock { Text = "每段独立加入队列并保留裁剪和其他编辑参数。Fast Copy 边界可能受关键帧影响。", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(note, 4); grid.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        buttons.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        _confirm = Ui.DialogButton("确定", () => { Refresh(); if (_segments is not null) Close(_segments); });
        buttons.Children.Add(_confirm); Grid.SetRow(buttons, 5); grid.Children.Add(buttons); Content = grid;
        _mode.SelectionChanged += (_, _) => Refresh(); _parts.ValueChanged += (_, _) => Refresh();
        _seconds.TextChanged += (_, _) => Refresh(); _points.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var mode = (ClipSplitMode)_mode.SelectedIndex;
        _partsPanel.IsVisible = mode == ClipSplitMode.EqualParts;
        _secondsPanel.IsVisible = mode == ClipSplitMode.FixedDuration;
        _pointsPanel.IsVisible = mode == ClipSplitMode.TimePoints;
        try
        {
            var count = _parts.Value ?? 2;
            if (mode == ClipSplitMode.EqualParts && count != decimal.Truncate(count)) throw new ArgumentException("段数必须为整数。");
            var settings = new ClipSplitSettings(mode, mode == ClipSplitMode.EqualParts ? (int)count : 2,
                mode == ClipSplitMode.FixedDuration ? ClipSplit.ParseTime(_seconds.Text) : 60,
                mode == ClipSplitMode.TimePoints ? ClipSplit.ParsePoints(_points.Text) : null);
            _segments = ClipSplit.Create(_draft, _duration, settings);
            if (!double.IsFinite(_draft.Speed) || _draft.Speed <= 0) throw new ArgumentException("速度无效，请先修正此文件的编辑参数。");
            _preview.Text = string.Join(Environment.NewLine, _segments.Select((s, i) =>
                $"{i + 1:00}    {Time(s.Start)} → {Time(s.End)}    输出 {MediaEngine.Number((s.End - s.Start) / s.Speed)} 秒"));
            _error.Text = ""; _confirm.IsEnabled = true;
        }
        catch (ArgumentException ex)
        {
            _segments = null; _preview.Text = ""; _error.Text = ex.Message; _confirm.IsEnabled = false;
        }
    }

    private static string Time(double seconds)=>MediaTime.Format(seconds);
}
