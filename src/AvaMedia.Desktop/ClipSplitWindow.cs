using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed class ClipSplitWindow : Window
{
    private readonly ConversionOptions _draft;
    private readonly double _duration;
    private readonly ComboBox _mode = Ui.Combo(["等分段数", "每段时长", "指定时间点"], "等分段数");
    private readonly NumericUpDown _parts = new() { Name = "SplitParts", Minimum = 2, Maximum = ClipSplit.MaximumSegments, Increment = 1, Value = 2, FormatString = "0" };
    private readonly NumericUpDown _seconds = Ui.Number(60,.01,86400,.1);
    private readonly SplitTimeline _timeline=new();
    private readonly StackPanel _partsPanel = new() { Spacing = 6 };
    private readonly StackPanel _secondsPanel = new() { Spacing = 6 };
    private readonly ListBox _preview=new(){Name="SplitPreview"};
    private readonly TextBlock _error = new() { Name = "SplitError", TextWrapping = TextWrapping.Wrap, Classes = { "error" } };
    private readonly Button _confirm;
    private IReadOnlyList<ConversionOptions>? _segments;

    public ClipSplitWindow(ConversionOptions draft, double duration,IMediaEngine? engine=null,string? source=null)
    {
        _draft = draft.Clone(); _duration = duration;
        Title = "分割"; Width = 920; Height = 760; MinWidth = 800; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _mode.Name = "SplitMode";
        var grid = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto"), Margin = new Thickness(20), RowSpacing = 12 };
        var end = draft.End > 0 ? draft.End : duration;
        var description = Ui.FormattedText($"源区间：{Time(draft.Start)} – {Time(end)} · {draft.Speed:0.###}×"); description.TextWrapping = TextWrapping.Wrap; grid.Children.Add(description);
        var modeRow = new Grid { ColumnDefinitions = new("100,*") }; modeRow.Children.Add(Ui.Text("分割方式"));
        Grid.SetColumn(_mode, 1); modeRow.Children.Add(_mode); Grid.SetRow(modeRow, 1); grid.Children.Add(modeRow);
        _partsPanel.Children.Add(Ui.FormattedText($"段数（2–{ClipSplit.MaximumSegments}）")); _partsPanel.Children.Add(Ui.Adjust(_parts));
        _secondsPanel.Children.Add(Ui.Text("每段时长（保留尾段）")); _secondsPanel.Children.Add(Ui.Adjust(_seconds,Math.Max(1,end-draft.Start)));
        _timeline.Start=draft.Start;_timeline.End=end;
        var fields = new StackPanel { Spacing = 6 }; fields.Children.Add(_partsPanel); fields.Children.Add(_secondsPanel);
        fields.Children.Add(Ui.Text("点击添加分割点，拖动调整；选中后按 Delete 删除。","caption"));fields.Children.Add(_timeline);
        Grid.SetRow(fields, 2); grid.Children.Add(fields);
        Control content=_preview;
        MediaPreviewPanel? mediaPreview=null;
        if(engine is not null&&source is not null)
        {
            var media=new MediaPreviewPanel(engine);mediaPreview=media;media.SetSource(source,draft);Closed+=(_,_)=>media.Dispose();
            var body=new Grid{ColumnDefinitions=new("*,*"),ColumnSpacing=12};body.Children.Add(_preview);Grid.SetColumn(media,1);body.Children.Add(media);content=body;
        }
        Grid.SetRow(content, 3);grid.Children.Add(content);
        var note = new StackPanel { Spacing = 6 }; note.Children.Add(_error);
        note.Children.Add(new TextBlock { Text = "每段单独导出，保留编辑参数。Fast Copy 切点受关键帧限制。", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(note, 4); grid.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        buttons.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        _confirm = Ui.DialogButton("确定", () => { Refresh(); if (_segments is not null) Close(_segments); });
        buttons.Children.Add(_confirm); Grid.SetRow(buttons, 5); grid.Children.Add(buttons); Content = grid;
        _mode.SelectionChanged += (_, _) => Refresh(); _parts.ValueChanged += (_, _) => Refresh();
        _seconds.ValueChanged += (_, _) => Refresh(); _timeline.Changed+=()=>{_mode.SelectedIndex=2;Refresh();if(_timeline.SelectedTime is {} seconds)mediaPreview?.SetPosition(seconds);};
        Refresh();
    }

    private void Refresh()
    {
        var mode = (ClipSplitMode)_mode.SelectedIndex;
        _partsPanel.IsVisible = mode == ClipSplitMode.EqualParts;
        _secondsPanel.IsVisible = mode == ClipSplitMode.FixedDuration;

        try
        {
            var count = _parts.Value ?? 2;
            if (mode == ClipSplitMode.EqualParts && count != decimal.Truncate(count)) throw new ArgumentException("段数必须为整数。");
            var settings = new ClipSplitSettings(mode, mode == ClipSplitMode.EqualParts ? (int)count : 2,
                mode == ClipSplitMode.FixedDuration ? (double)(_seconds.Value??60) : 60,
                mode == ClipSplitMode.TimePoints ? _timeline.Cuts.Order().Distinct().ToArray() : null);
            _segments = ClipSplit.Create(_draft, _duration, settings);
            if (!double.IsFinite(_draft.Speed) || _draft.Speed <= 0) throw new ArgumentException("速度无效，请先修正此文件的编辑参数。");
            if(mode!=ClipSplitMode.TimePoints)_timeline.SetCuts(_segments.Skip(1).Select(s=>s.Start));
            _preview.ItemsSource = _segments.Select((s, i) =>
                Localization.Format($"{i + 1:00}    {Time(s.Start)} → {Time(s.End)}    输出 {MediaEngine.Number((s.End - s.Start) / s.Speed)} 秒")).ToArray();
            _error.Text = ""; _confirm.IsEnabled = true;
        }
        catch (ArgumentException ex)
        {
            _segments = null; _preview.ItemsSource = null; _error.Text = ex.Message; _confirm.IsEnabled = false;
        }
    }

    private static string Time(double seconds)=>MediaTime.Format(seconds);
}
