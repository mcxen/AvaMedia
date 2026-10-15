using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Host actions that open other windows; null hides the action.</summary>
internal sealed record PersonClipTaskActions(Func<Task>? OpenOutput = null, Func<Task>? AdjustSegments = null, Func<Task>? EditParameters = null);

/// <summary>保留有人片段 content slot for the multi-stage shell: preview, timeline, statistics, kept segments and read-only settings.</summary>
internal sealed class PersonClipStageContent : Grid, IMultiStageToolContent, ISegmentThumbnailSource
{
    private sealed record SegmentRow(PersonClipKeptSegment Segment, ClipSegmentEntry Entry, bool Live);

    private readonly IPersonClipTask _task;
    private readonly IMediaEngine _engine;
    private readonly PersonClipTaskActions _actions;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PreviewRequest _previewRequest = new();
    // Preview card
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly MediaPreviewSurface _surface = new() { UseAmbientLighting = false };
    private readonly Border _dim = new() { IsHitTestVisible = false, IsVisible = false, Opacity = .55 };
    private readonly DetectionOverlay _boxes = new();
    private readonly PreviewChip _chipLeft = new(HorizontalAlignment.Left), _chipRight = new(HorizontalAlignment.Right);
    private readonly TextBlock _placeholder = new() { Classes = { "ms-caption" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _caption = new() { Classes = { "ms-caption" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _captionRight = new() { Classes = { "ms-caption" }, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _replayNavigation = new() { ColumnDefinitions = new("Auto,*,Auto"), IsVisible = false };
    private readonly TextBlock _replayPosition = new() { Classes = { "ms-small" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _replayDetail = new() { Classes = { "ms-caption" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _previousFrame, _nextFrame;
    private int _replayIndex;
    private bool _processingCompleted;
    private readonly TextBlock _configNote = new() { Classes = { "ms-small" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
    // Timeline card
    private readonly StageTimeline _timeline = new() { Margin = new(0, 0, 0, 0) };
    private readonly StackPanel _legendUnscanned = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    private readonly TextBlock[] _ticks = Enumerable.Range(0, 5).Select(_ => new TextBlock { Classes = { "ms-tick" } }).ToArray();
    private readonly UniformGrid _strip = new() { Rows = 1, Margin = new(-3, 7, -3, 0) };
    private readonly TextBlock _ratioLabel = new() { Classes = { "ms-caption" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _ratioValue = new() { Classes = { "ms-caption" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _ratioBar = new() { Classes = { "ac" }, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _ratioRight = new() { Classes = { "ms-caption" }, VerticalAlignment = VerticalAlignment.Center };
    // Stats
    private readonly TextBlock[] _statLabels = Enumerable.Range(0, 4).Select(_ => new TextBlock { Classes = { "ms-small" } }).ToArray();
    private readonly TextBlock[] _statValues = Enumerable.Range(0, 4).Select(_ => new TextBlock { Classes = { "ms-stat" } }).ToArray();
    // Segment list
    private readonly Border _listCard = new() { Classes = { "ms-card" }, ClipToBounds = true };
    private readonly TextBlock _listTitle = new() { Classes = { "ms-heading" } };
    private readonly TextBlock _listNote = new() { Classes = { "ms-small" }, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ListBox _list = new() { Classes = { "ms-segments" } };
    private readonly EmptyState _listEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ObservableCollection<SegmentRow> _rows = [];
    // Layout switching
    private readonly Panel _runRight = new StackPanel(), _configRight = new StackPanel();
    private readonly StackPanel _settings = new();
    private readonly Button _editParameters;
    // Output and actions
    private readonly StackPanel _output = new() { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _outputPath = new() { Classes = { "ms-caption" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _changeOutput, _reveal, _adjust, _adjustPrimary, _open;
    private readonly Dictionary<double, ClipSegmentEntry> _entries = [];
    private PersonClipTaskView _view = new();
    private MultiStageTaskSnapshot _snapshot = new();
    private string _previewKey = "";
    private int _selected = -1;
    private bool _disposed, _selecting;

    public PersonClipStageContent(IPersonClipTask task, IMediaEngine engine, PersonClipTaskActions actions)
    {
        _task = task; _engine = engine; _actions = actions;
        _previousFrame = Ui.Button("上一帧", () => BrowseFrame(-1));
        _nextFrame = Ui.Button("下一帧", () => BrowseFrame(1));
        foreach (var button in new[] { _previousFrame, _nextFrame }) button.Classes.Add("text-action");
        _replayNavigation.Bind(ColumnSpacingProperty, new DynamicResourceExtension("AcRowGap"));
        _replayNavigation.Children.Add(_previousFrame);
        Grid.SetColumn(_replayPosition, 1); _replayNavigation.Children.Add(_replayPosition);
        Grid.SetColumn(_nextFrame, 2); _replayNavigation.Children.Add(_nextFrame);
        RowDefinitions = new("Auto,*");
        Bind(RowSpacingProperty, new DynamicResourceExtension("AcMsGap"));
        var top = new Grid { ColumnDefinitions = new("Auto,*"), VerticalAlignment = VerticalAlignment.Top };
        top.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("AcMsGap"));
        var left = new StackPanel { Spacing = 10 };
        left.Bind(WidthProperty, new DynamicResourceExtension("AcMsPreviewWidth"));
        left.Children.Add(BuildPreview()); left.Children.Add(_configNote);
        _configNote.Text = Localization.Text("阶段用时与剩余时间在开始后按真实进度显示，开始前不估算。");
        top.Children.Add(left);
        var right = new Panel(); Grid.SetColumn(right, 1); top.Children.Add(right);
        ((StackPanel)_runRight).Spacing = 12;
        _runRight.Children.Add(BuildTimeline()); _runRight.Children.Add(BuildStats()); right.Children.Add(_runRight);
        _editParameters = Ui.Button("编辑参数…", () => _ = RunAsync(_actions.EditParameters));
        _editParameters.Classes.Add("small"); _editParameters.Classes.Add("text-action");
        right.Children.Add(BuildConfig());
        Children.Add(top);
        Grid.SetRow(_listCard, 1); _listCard.Child = BuildList(); Children.Add(_listCard);

        _changeOutput = Ui.Button("更改…", () => _ = ChangeOutputAsync()); _changeOutput.Classes.Add("link"); _changeOutput.Classes.Add("text-action");
        _reveal = Ui.Button("在文件夹中显示", () => { if (File.Exists(_view.OutputPath)) PlatformServices.RevealFile(_view.OutputPath); });
        _adjust = Ui.Button("调整片段", () => _ = RunAsync(_actions.AdjustSegments));
        _adjustPrimary = Ui.Button("调整片段", () => _ = RunAsync(_actions.AdjustSegments)); _adjustPrimary.Classes.Add("primary");
        _open = Ui.Button("打开视频", () => _ = RunAsync(_actions.OpenOutput)); _open.Classes.Add("primary");
        BuildOutput();
        _timeline.SelectSegment += index => Select(index, true);
        _list.SelectionChanged += (_, _) => { if (!_selecting && _list.SelectedItem is SegmentRow row) Select(row.Segment.Number - 1, false); };
    }

    public Control Result => this;
    public Control Output => _output;
    public IReadOnlyList<Button> LeadingActions => [_reveal, _adjust];
    public IReadOnlyList<Button> TrailingActions => [_open, _adjustPrimary];
    public string SourceTitle => Path.GetFileName(_task.View.SourcePath);
    public string SourceTooltip => _task.View.SourcePath;
    public IReadOnlyList<string> InputPaths => _task.View.SourcePath.Length > 0 ? [_task.View.SourcePath] : [];
    public IReadOnlyList<string> OutputPaths => _view.OutputExists && File.Exists(_view.OutputPath) ? [_view.OutputPath] : [];

    private Control BuildPreview()
    {
        var card = new Border { Classes = { "ms-card", "ms-section" } };
        var stack = new StackPanel();
        var frame = new Grid();
        frame.Bind(BackgroundProperty, new DynamicResourceExtension("AcPreviewFill"));
        frame.Children.Add(_image); frame.Children.Add(_placeholder);
        _dim.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiMediaOverlay"));
        frame.Children.Add(_dim); frame.Children.Add(_boxes); frame.Children.Add(_chipLeft); frame.Children.Add(_chipRight);
        _placeholder.Text = Localization.Text("暂无预览");
        _surface.PreviewContent = frame;
        _surface.Bind(MediaPreviewSurface.ContentSizeProperty, new Binding("Source.Size") { Source = _image });
        stack.Children.Add(_surface);
        stack.SizeChanged += (_, e) => { if (e.NewSize.Width > 0) _surface.Height = Math.Round(e.NewSize.Width * 9 / 16); };
        var caption = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new(0, 10, 0, 0) };
        caption.Children.Add(_caption); Grid.SetColumn(_captionRight, 1); caption.Children.Add(_captionRight);
        stack.Children.Add(caption);
        _replayDetail.Margin = new(0, 8, 0, 0);
        _replayNavigation.Margin = new(0, 8, 0, 0);
        stack.Children.Add(_replayDetail); stack.Children.Add(_replayNavigation);
        card.Child = stack; return card;
    }

    private Control BuildTimeline()
    {
        var card = new Border { Classes = { "ms-card", "ms-section" } };
        var stack = new StackPanel();
        var head = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new(0, 0, 0, 9) };
        head.Children.Add(new TextBlock { Classes = { "ms-heading" }, Text = Localization.Text("人物出现时间轴"), VerticalAlignment = VerticalAlignment.Center });
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(Legend(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }, "有人 · 保留", "UiAccent"));
        legend.Children.Add(Legend(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }, "无人 · 移除", "UiTrack"));
        legend.Children.Add(Legend(_legendUnscanned, "未检测", "AcUnscannedFill", true));
        Grid.SetColumn(legend, 1); head.Children.Add(legend);
        stack.Children.Add(head); stack.Children.Add(_timeline);
        var ticks = new Grid { ColumnDefinitions = new("*,*,*,*,*"), Margin = new(0, 5, 0, 0) };
        for (var i = 0; i < 5; i++)
        {
            _ticks[i].HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            Grid.SetColumn(_ticks[i], i); ticks.Children.Add(_ticks[i]);
        }
        stack.Children.Add(ticks); stack.Children.Add(_strip);
        var ratio = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 10, Margin = new(0, 10, 0, 0) };
        ratio.Children.Add(_ratioLabel); Grid.SetColumn(_ratioValue, 1); ratio.Children.Add(_ratioValue);
        _ratioBar.Bind(HeightProperty, new DynamicResourceExtension("AcProgressHeight"));
        Grid.SetColumn(_ratioBar, 2); ratio.Children.Add(_ratioBar); Grid.SetColumn(_ratioRight, 3); ratio.Children.Add(_ratioRight);
        stack.Children.Add(ratio);
        card.Child = stack; return card;
    }

    private static StackPanel Legend(StackPanel row, string text, string brush, bool outline = false)
    {
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
        dot.Bind(Border.BackgroundProperty, new DynamicResourceExtension(brush));
        if (outline) { dot.BorderThickness = new(.5); dot.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AcHairline")); }
        row.Children.Add(dot); row.Children.Add(new TextBlock { Classes = { "ms-small" }, Text = Localization.Text(text), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private Control BuildStats()
    {
        var card = new Border { Classes = { "ms-card" }, ClipToBounds = true };
        var grid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto") };
        for (var i = 0; i < 4; i++)
        {
            var cell = new Border { Classes = { "ms-row-line" }, Padding = new(14, 9), BorderThickness = new(i % 2 == 1 ? .5 : 0, i >= 2 ? .5 : 0, 0, 0) };
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(_statLabels[i]); stack.Children.Add(_statValues[i]);
            cell.Child = stack; Grid.SetRow(cell, i / 2); Grid.SetColumn(cell, i % 2); grid.Children.Add(cell);
        }
        card.Child = grid; return card;
    }

    private Control BuildConfig()
    {
        var panel = (StackPanel)_configRight; panel.Spacing = 9;
        var head = new Grid { ColumnDefinitions = new("*,Auto") };
        head.Children.Add(new TextBlock { Classes = { "ms-heading" }, Text = Localization.Text("检测设置"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_editParameters, 1); head.Children.Add(_editParameters);
        panel.Children.Add(head);
        panel.Children.Add(new Border { Classes = { "ms-group" }, Child = _settings });
        return panel;
    }

    private Control BuildList()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var head = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Margin = new(14, 11, 14, 6) };
        head.Children.Add(_listTitle); Grid.SetColumn(_listNote, 1); head.Children.Add(_listNote);
        _listNote.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(head);
        _list.ItemsSource = _rows;
        _list.ItemTemplate = new FuncDataTemplate<SegmentRow>((row, _) => row is null ? null : Row(row));
        Avalonia.Automation.AutomationProperties.SetName(_list, Localization.Text("保留片段"));
        var area = new Panel(); area.Children.Add(_list); area.Children.Add(_listEmpty);
        Grid.SetRow(area, 1); grid.Children.Add(area);
        return grid;
    }

    private static Control Row(SegmentRow row)
    {
        var grid = new Grid { ColumnDefinitions = new("22,56,*,Auto"), ColumnSpacing = 12, Margin = new(14, 5) };
        var number = new TextBlock { Text = row.Segment.Number.ToString(CultureInfo.InvariantCulture), FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        number.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AcTextTertiary"));
        grid.Children.Add(number);
        var thumb = new Border { Width = 56, Height = 31.5, CornerRadius = new(5), ClipToBounds = true, Child = new SegmentThumbnail { DataContext = row.Entry, Stretch = Stretch.UniformToFill } };
        thumb.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AcPreviewFill"));
        Grid.SetColumn(thumb, 1); grid.Children.Add(thumb);
        var range = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        range.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
        range.Inlines = [new Run($"{PersonTaskView.Time(row.Segment.Start)} — {PersonTaskView.Time(row.Segment.End)}")];
        if (row.Live)
        {
            var live = new Run("  " + Localization.Text("检测中")) { FontSize = 10, FontWeight = FontWeight.SemiBold };
            live.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("UiAccent"));
            range.Inlines.Add(live);
        }
        Grid.SetColumn(range, 2); grid.Children.Add(range);
        var duration = new TextBlock { Classes = { "ms-caption" }, Text = Localization.Format($"{row.Segment.Duration:0.0} 秒"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(duration, 3); grid.Children.Add(duration);
        return new Border { Child = grid, Classes = { "ms-row-line" }, BorderThickness = new(0, row.Segment.Number == 0 ? 0 : .5, 0, 0) };
    }

    private void BuildOutput()
    {
        var icon = new LucideIcon { Kind = "folder-closed", Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiAccent"));
        _output.Children.Add(icon);
        _output.Children.Add(new TextBlock { Classes = { "ms-caption" }, Text = Localization.Text("输出到"), VerticalAlignment = VerticalAlignment.Center });
        _outputPath.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
        Localization.SetIsUserText(_outputPath, true);
        _output.Children.Add(_outputPath); _output.Children.Add(_changeOutput);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public void SetProcessingCompleted(bool completed)
    {
        if (_processingCompleted == completed) return;
        _processingCompleted = completed;
        Update(_snapshot);
    }

    public MultiStageContentState Update(MultiStageTaskSnapshot snapshot)
    {
        _snapshot = snapshot; _view = _task.View;
        var view = _view; var state = snapshot.State;
        var configuring = state == MultiStageTaskState.Configuring;
        var done = _processingCompleted || state == MultiStageTaskState.Completed;
        var live = !done && state is MultiStageTaskState.Running or MultiStageTaskState.Paused or MultiStageTaskState.Stopping;
        var duration = view.Info?.Duration ?? 0;
        _runRight.IsVisible = _listCard.IsVisible = !configuring;
        _configRight.IsVisible = _configNote.IsVisible = configuring;
        _editParameters.IsVisible = _actions.EditParameters is not null;
        if (configuring) RefreshSettings(view.Settings);

        // Timeline
        var partial = !done || view.IsPartial;
        _timeline.Update(view.Segments, duration, view.AnalyzedSeconds, partial, state == MultiStageTaskState.Failed);
        _legendUnscanned.IsVisible = partial;
        for (var i = 0; i < 5; i++) _ticks[i].Text = duration > 0 ? PersonTaskView.Time(duration * i / 4, 0) : "—";
        var kept = view.KeptSeconds;
        _ratioLabel.Text = Localization.Text(done ? "保留" : "暂保留");
        _ratioValue.Inlines = [new Run(PersonTaskView.Time(kept, 1)) { FontWeight = FontWeight.SemiBold, [!TextElement.ForegroundProperty] = new DynamicResourceExtension("UiText") }];
        if (done && duration > 0) _ratioValue.Inlines.Add(new Run(" / " + PersonTaskView.Time(duration, 1)));
        _ratioBar.Value = duration > 0 ? Math.Clamp(kept / duration, 0, 1) : 0;
        _ratioRight.Text = done ? (duration > 0 ? Localization.Format($"{kept / duration:0%} · 移除 {PersonTaskView.Time(Math.Max(0, duration - kept), 1)}") : "")
            : state == MultiStageTaskState.Failed ? Localization.Format($"中断于 {PersonTaskView.Time(view.AnalyzedSeconds, 1)}")
            : state == MultiStageTaskState.Cancelled ? Localization.Format($"取消于 {PersonTaskView.Time(view.AnalyzedSeconds, 1)}")
            : Localization.Format($"已检测至 {PersonTaskView.Time(view.AnalyzedSeconds, 1)}");

        RefreshStats(view, snapshot, done);
        RefreshRows(view, live, done);
        RefreshPreview(view, state);

        // Output
        var outputFile = done && view.OutputExists;
        _outputPath.Text = Display(outputFile ? view.OutputPath : view.OutputFolder.Length > 0 ? view.OutputFolder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar : "—");
        ToolTip.SetTip(_outputPath, outputFile ? view.OutputPath : view.OutputFolder);
        _changeOutput.IsVisible = configuring && snapshot.Commands.CanChangeOutput;
        var analysisOnly = view.Settings.AnalysisOnly;
        _reveal.IsVisible = outputFile && !analysisOnly;
        _adjust.IsVisible = done && view.CanAdjust && _actions.AdjustSegments is not null && !analysisOnly;
        _open.IsVisible = outputFile && !analysisOnly && _actions.OpenOutput is not null;
        _adjustPrimary.IsVisible = done && analysisOnly && view.CanAdjust && _actions.AdjustSegments is not null;

        var info = view.Info;
        var detail = Localization.Join(" · ", new[]
        {
            info is { Width: > 0, Height: > 0 } ? $"{info.Width}×{info.Height}" : "", info is null ? "" : Codec(info.VideoCodec),
            duration > 0 ? PersonTaskView.Time(duration, 1) : "",
            view.Settings.ModelName.Length > 0 ? Localization.Format($"{view.Settings.ModelName} 人物检测") : "", Localization.Text(view.Settings.ModeLabel)
        }.Where(s => s.Length > 0));
        var stages = snapshot.Stages;
        var failedStage = stages.FirstOrDefault(stage => stage.Stage.Id == snapshot.InterruptedStageId)?.Stage;
        var errorTitle = failedStage is null ? "" : failedStage.Id == PersonClipStageIds.Detect && view.AnalyzedSeconds > 0
            ? Localization.Format($"{Localization.Text(failedStage.Title)}在 {PersonTaskView.Time(view.AnalyzedSeconds, 1)} 处中断：")
            : Localization.Format($"{Localization.Text(failedStage.Title)}失败：");
        var retained = stages.Where(stage => snapshot.RetainedStageIds.Contains(stage.Stage.Id)).Select(stage => Localization.Text(stage.Stage.Title)).ToArray();
        var retry = stages.FirstOrDefault(stage => stage.Stage.Id == snapshot.Commands.RetryStageId)?.Stage.Title;
        var recovery = retry is null ? (snapshot.Commands.CanRerun ? Localization.Text("可重新执行全部阶段。") : "")
            : (retained.Length > 0 ? Localization.Format($"{string.Join(Localization.Text("与"), retained)}的结果已保留，") : "")
              + Localization.Format($"可从{Localization.Text(retry)}阶段重试。");
        var note = state switch
        {
            MultiStageTaskState.Configuring => Localization.Text("开始前确认参数"),
            MultiStageTaskState.Running or MultiStageTaskState.Stopping => Localization.Format($"正在{Localization.Text(CurrentTitle(snapshot))} · 结果随进度增加"),
            MultiStageTaskState.Paused => Localization.Text("已暂停 · 已有结果保留"),
            MultiStageTaskState.Failed or MultiStageTaskState.Cancelled => Localization.Text(view.Segments.Count > 0 ? "部分结果可查看，不能导出" : "没有可查看的结果"),
            _ => analysisOnly ? Localization.Format($"仅检测 · {view.Segments.Count} 个片段")
                : view.OutputBytes is { } bytes ? Localization.Format($"输出 1 个文件 · {Size(bytes)}") : ""
        };
        var plan = Localization.Join(" · ", new[] { Localization.Format($"{stages.Count} 个阶段"), view.Settings.ModelName }.Where(s => s.Length > 0));
        return new(detail, note, plan, errorTitle, recovery, _open.IsVisible || _adjustPrimary.IsVisible);
    }

    private static string CurrentTitle(MultiStageTaskSnapshot snapshot) =>
        snapshot.CurrentStageIndex >= 0 && snapshot.CurrentStageIndex < snapshot.Stages.Count ? snapshot.Stages[snapshot.CurrentStageIndex].Stage.Title : "处理";

    private void RefreshStats(PersonClipTaskView view, MultiStageTaskSnapshot snapshot, bool done)
    {
        string[] labels = done ? ["保留片段", "保留时长", "用时", "检测帧数"] : ["已保留片段", "暂保留时长", "已用时", "检测帧数"];
        for (var i = 0; i < 4; i++) _statLabels[i].Text = Localization.Text(labels[i]);
        var hasResult = view.HasResult || view.Segments.Count > 0 || view.SampledFrames > 0;
        var duration = view.Info?.Duration ?? 0;
        Value(0, hasResult ? view.Segments.Count.ToString(CultureInfo.InvariantCulture) : "—", "");
        Value(1, hasResult ? PersonTaskView.Time(view.KeptSeconds, 1) : "—", done && duration > 0 ? "/ " + PersonTaskView.Time(duration, 1) : "");
        var elapsed = snapshot.Elapsed.TotalSeconds;
        Value(2, elapsed > 0 ? Localization.Format($"{(done ? MultiStageTaskWindow.Seconds(elapsed) : Math.Floor(elapsed).ToString(CultureInfo.InvariantCulture))} 秒") : "—", "");
        Value(3, hasResult ? view.SampledFrames.ToString(CultureInfo.InvariantCulture) : "—", !done && view.PlannedFrames is { } planned ? "/ " + planned.ToString(CultureInfo.InvariantCulture) : "");
    }

    private void Value(int index, string value, string suffix)
    {
        var block = _statValues[index];
        block.Inlines = [new Run(value)];
        if (suffix.Length == 0) return;
        var small = new Run(" " + suffix) { FontSize = 11, FontWeight = FontWeight.Normal };
        small.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        block.Inlines.Add(small);
    }

    private void RefreshRows(PersonClipTaskView view, bool live, bool done)
    {
        _listTitle.Text = Localization.Text(live ? "新保留的片段" : done ? "保留片段" : "已保留片段");
        var duration = view.Info?.Duration ?? 0;
        _listNote.Text = done && duration > 0
            ? Localization.Format($"保留 {view.KeptSeconds / duration:0%} · 移除 {PersonTaskView.Time(Math.Max(0, duration - view.KeptSeconds), 1)}")
            : view.Segments.Count == 0 ? "" : Localization.Format($"{view.Segments.Count} 段 · ") + Localization.Text(live ? "检测中持续增加" : "部分结果");
        IEnumerable<PersonClipKeptSegment> ordered = done ? view.Segments : view.Segments.Reverse();
        var rows = ordered.Select(segment => new SegmentRow(segment, Entry(segment, view), live && segment.IsOpen)).ToArray();
        if (!rows.Select(row => (row.Segment, row.Live)).SequenceEqual(_rows.Select(row => (row.Segment, row.Live))))
        {
            _selecting = true;
            try
            {
                var common = 0;
                while (common < rows.Length && common < _rows.Count && rows[common].Segment == _rows[common].Segment && rows[common].Live == _rows[common].Live) common++;
                while (_rows.Count > common) _rows.RemoveAt(_rows.Count - 1);
                foreach (var row in rows.Skip(common)) _rows.Add(row);
            }
            finally { _selecting = false; }
        }
        _listEmpty.IsVisible = rows.Length == 0;
        _listEmpty.Text = Localization.Text(done ? "没有可保留片段" : "尚无保留片段");
        if (done && view.Segments.Count > 0 && (_selected < 0 || _selected >= view.Segments.Count)) Select(0, false);
        if (!done && _selected >= 0) { _selected = -1; _timeline.Selected = -1; }
        RefreshStrip(view, done);
    }

    private ClipSegmentEntry Entry(PersonClipKeptSegment segment, PersonClipTaskView view)
    {
        var start = segment.Start;
        if (_entries.TryGetValue(start, out var entry) && Math.Abs(entry.End - segment.End) < 1e-6) return entry;
        entry = new ClipSegmentEntry(new ConversionOptions { Start = start, End = segment.End, VideoStreamIndex = view.Info?.VideoStreamIndex ?? 0 }, view.SourcePath)
        { Number = segment.Number, SourceDuration = view.Info?.Duration ?? 0 };
        _entries[start] = entry; return entry;
    }

    private void RefreshStrip(PersonClipTaskView view, bool done)
    {
        _strip.IsVisible = done && view.Segments.Count > 0;
        if (!_strip.IsVisible) { _strip.Children.Clear(); return; }
        var count = Math.Min(8, view.Segments.Count);
        var first = Math.Clamp(_selected - 4, 0, Math.Max(0, view.Segments.Count - count));
        var keys = Enumerable.Range(first, count).ToArray();
        if (_strip.Tag is int[] shown && shown.SequenceEqual(keys) && _strip.Children.Count == count)
        {
            for (var i = 0; i < count; i++) Mark((Border)_strip.Children[i], keys[i] == _selected);
            return;
        }
        _strip.Tag = keys; _strip.Children.Clear(); _strip.Columns = 8; // fixed cells: short results stay thumbnail-sized
        foreach (var index in keys)
        {
            var segment = view.Segments[index];
            var thumb = new Border { Margin = new(3), CornerRadius = new(6), ClipToBounds = true, Height = 34, Tag = index,
                Child = new SegmentThumbnail { DataContext = Entry(segment, view), Stretch = Stretch.UniformToFill }, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            thumb.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AcPreviewFill"));
            ToolTip.SetTip(thumb, Localization.Format($"片段 {segment.Number}"));
            thumb.PointerPressed += (_, _) => Select(index, true);
            Mark(thumb, index == _selected); _strip.Children.Add(thumb);
        }
        static void Mark(Border thumb, bool selected)
        {
            thumb.BorderThickness = new(selected ? 2 : 0);
            thumb.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiAccent"));
        }
    }

    private void Select(int index, bool scroll)
    {
        if (_snapshot.State != MultiStageTaskState.Completed || index < 0 || index >= _view.Segments.Count) return;
        _selected = index; _timeline.Selected = index;
        _selecting = true;
        try
        {
            var row = _rows.FirstOrDefault(row => row.Segment.Number - 1 == index);
            _list.SelectedItem = row;
            if (scroll && row is not null) _list.ScrollIntoView(row);
        }
        finally { _selecting = false; }
        RefreshStrip(_view, true);
        RefreshPreview(_view, _snapshot.State);
    }

    private void RefreshPreview(PersonClipTaskView view, MultiStageTaskState state)
    {
        if (_processingCompleted || state == MultiStageTaskState.Completed)
        {
            RefreshReplay(view);
            return;
        }
        _replayNavigation.IsVisible = _replayDetail.IsVisible = false;
        _placeholder.Text = Localization.Text("暂无预览");
        var info = view.Info;
        var stream = info?.VideoStreamIndex ?? 0;
        _dim.IsVisible = state == MultiStageTaskState.Failed;
        _boxes.IsVisible = false; _chipRight.Text = ""; _chipLeft.Text = "";
        string caption = "", detail = "", right = "";
        switch (state)
        {
            case MultiStageTaskState.Configuring:
                LoadThumbnail(view.SourcePath, 0, stream);
                _chipRight.Text = info?.Duration > 0 ? PersonTaskView.Time(info.Duration, 0) : "";
                caption = "首帧";
                detail = Localization.Join(" · ", new[] { info is { Width: > 0 } ? $"{info.Width}×{info.Height}" : "", info?.FrameRate > 0 ? $"{info.FrameRate:0.##} fps" : "" }.Where(s => s.Length > 0));
                right = info?.Duration > 0 ? Localization.Format($"时长 {PersonTaskView.Time(info.Duration, 1)}") : "";
                break;
            case MultiStageTaskState.Running or MultiStageTaskState.Paused or MultiStageTaskState.Stopping:
                if (view.LiveFrame is { } frame)
                {
                    LoadEncoded(frame.Image);
                    _boxes.IsVisible = frame.Boxes.Count > 0; _boxes.Update(frame.Boxes);
                    _chipLeft.Text = Localization.Format($"{PersonTaskView.Time(frame.Seconds, 1)} · 第 {frame.FrameNumber} 帧");
                    detail = Localization.Join(" · ", new[] { Localization.Format($"{frame.PersonCount} 个人物"),
                        view.Settings.Threshold > 0 ? Localization.Format($"置信度 ≥ {view.Settings.Threshold:0.##}") : "" }.Where(s => s.Length > 0));
                }
                else LoadThumbnail(view.SourcePath, 0, stream);
                caption = "实时预览";
                right = view.Settings.FramesPerSecond > 0 ? Localization.Format($"每秒 {view.Settings.FramesPerSecond:0.##} 帧") : "";
                break;
            case MultiStageTaskState.Failed or MultiStageTaskState.Cancelled:
                var at = Math.Max(0, view.AnalyzedSeconds - .05);
                if (view.LiveFrame is { } last && Math.Abs(last.Seconds - view.AnalyzedSeconds) < 1) LoadEncoded(last.Image);
                else LoadThumbnail(view.SourcePath, at, stream);
                var failed = state == MultiStageTaskState.Failed;
                _chipLeft.Text = Localization.Format($"{PersonTaskView.Time(view.AnalyzedSeconds, 1)} · ") + Localization.Text(failed ? "最后可读帧" : "取消时画面");
                caption = failed ? "中断处画面" : "取消时画面";
                detail = Localization.Format($"已检测 {view.SampledFrames} 帧");
                right = view.Segments.Count > 0 ? Localization.Format($"已保留 {view.Segments.Count} 段") : "";
                break;
            default:
                if (_selected >= 0 && _selected < view.Segments.Count)
                {
                    var segment = view.Segments[_selected];
                    LoadThumbnail(view.SourcePath, (segment.Start + segment.End) / 2, stream);
                    _chipLeft.Text = Localization.Format($"片段 {segment.Number} · {PersonTaskView.Time(segment.Start, 1)} — {PersonTaskView.Time(segment.End, 1)}");
                    caption = Localization.Format($"片段 {segment.Number}");
                    detail = Localization.Format($"{segment.Duration:0.0} 秒");
                    right = Localization.Format($"{view.Segments.Count} 段中的第 {segment.Number} 段");
                }
                else { LoadThumbnail(view.SourcePath, 0, stream); caption = "首帧"; }
                break;
        }
        _caption.Inlines = [new Run(Localization.Text(caption)) { FontWeight = FontWeight.Medium, [!TextElement.ForegroundProperty] = new DynamicResourceExtension("UiText") }];
        if (detail.Length > 0) _caption.Inlines.Add(new Run("  ·  " + detail));
        _captionRight.Text = right;
    }

    private void LoadThumbnail(string path, double seconds, int stream)
    {
        var key = $"t:{path}:{seconds:0.000}:{stream}";
        if (_previewKey == key || path.Length == 0) return;
        _previewKey = key;
        _ = LoadAsync(async token =>
        {
            var data = await _engine.Thumbnail(path, seconds, VideoPreviewSizes.Compact.Width, VideoPreviewSizes.Compact.Height, token, pad: false, videoStreamIndex: stream).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var memory = new MemoryStream(data); return new Bitmap(memory);
        });
    }

    private void BrowseFrame(int direction)
    {
        if (_view.ReplayFrames.Count == 0) return;
        _replayIndex = Math.Clamp(_replayIndex + direction, 0, _view.ReplayFrames.Count - 1);
        RefreshReplay(_view);
    }

    private void RefreshReplay(PersonClipTaskView view)
    {
        _dim.IsVisible = false;
        _chipLeft.Text = _chipRight.Text = "";
        _caption.Inlines = null; _caption.Text = Localization.Text("检测画面"); _captionRight.Text = "";
        var count = view.ReplayFrames.Count;
        _replayNavigation.IsVisible = _replayDetail.IsVisible = count > 0;
        if (count == 0)
        {
            _previewKey = ""; _previewRequest.Cancel();
            var old = _image.Source as Bitmap; _image.Source = null; old?.Dispose();
            _boxes.IsVisible = false;
            _placeholder.Text = Localization.Text("此任务未保存检测画面");
            _placeholder.TextWrapping = TextWrapping.Wrap;
            _placeholder.IsVisible = true;
            return;
        }
        _replayIndex = Math.Clamp(_replayIndex, 0, count - 1);
        var frame = view.ReplayFrames[_replayIndex];
        _previousFrame.IsEnabled = _replayIndex > 0; _nextFrame.IsEnabled = _replayIndex < count - 1;
        _replayPosition.Text = $"{_replayIndex + 1} / {count}";
        var sample = frame.Sample;
        _chipLeft.Text = PersonTaskView.Time(sample.Seconds, 3);
        _replayDetail.Text = Localization.Format($"得分 {sample.Score:0.000}");
        var flag = sample.Flags.HasFlag(PersonClipSampleFlags.Dark) ? "黑灯"
            : sample.Flags.HasFlag(PersonClipSampleFlags.Blank) ? "无画面"
            : sample.Flags.HasFlag(PersonClipSampleFlags.Uncertain) ? "待确认"
            : sample.Flags.HasFlag(PersonClipSampleFlags.Person) ? "保留" : "跳过";
        _replayDetail.Text += " · " + Localization.Text(flag);
        if (sample.Flags.HasFlag(PersonClipSampleFlags.Boundary)) _replayDetail.Text += " · " + Localization.Text("边界画面");
        if (sample.Flags.HasFlag(PersonClipSampleFlags.Reused)) _replayDetail.Text += " · " + Localization.Text("复用得分");
        var key = "replay:" + frame.ThumbnailPath;
        if (_previewKey == key) return;
        _previewKey = key;
        var previous = _image.Source as Bitmap; _image.Source = null; previous?.Dispose();
        _boxes.IsVisible = false;
        _placeholder.Text = Localization.Text("检测画面不可用"); _placeholder.IsVisible = true;
        _ = LoadAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new Bitmap(frame.ThumbnailPath));
        }, () => { _boxes.Update(frame.Boxes); _boxes.IsVisible = frame.Boxes.Count > 0; });
    }

    private void LoadEncoded(byte[] image)
    {
        var key = "e:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(image).ToString(CultureInfo.InvariantCulture);
        if (_previewKey == key) return;
        _previewKey = key;
        _ = LoadAsync(token => { using var memory = new MemoryStream(image); return Task.FromResult(new Bitmap(memory)); });
    }

    private async Task LoadAsync(Func<CancellationToken, Task<Bitmap>> decode, Action? loaded = null)
    {
        if (_disposed) return;
        var token = _previewRequest.Restart(_lifetime.Token);
        try
        {
            // Native decoding and file/process work stay off the UI thread.
            var bitmap = await Task.Run(() => decode(token), token);
            if (_disposed || !_previewRequest.IsCurrent(token)) { bitmap.Dispose(); return; }
            var old = _image.Source as Bitmap; _image.Source = bitmap; old?.Dispose(); _placeholder.IsVisible = false;
            loaded?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppDiagnostics.Record("Multi-stage preview", error);
            if (_previewRequest.IsCurrent(token)) { var old = _image.Source as Bitmap; _image.Source = null; old?.Dispose(); _placeholder.IsVisible = true; }
        }
    }

    private void RefreshSettings(PersonClipSettingsSummary settings)
    {
        var installed = settings.ModelInstalled switch { true => " · " + Localization.Text("已下载"), false => " · " + Localization.Text("未下载"), _ => "" };
        var rows = new (string Label, string Value, string Unit, bool? Toggle)[]
        {
            ("检测模型", settings.ModelName, installed, null),
            ("检测方式", Localization.Text(settings.ModeLabel), "", null),
            ("每秒采样帧数", Number(settings.FramesPerSecond), "", null),
            ("检测阈值", Number(settings.Threshold), "", null),
            ("前后保留", Number(settings.PaddingSeconds), " 秒", null),
            ("合并间隔", Number(settings.MergeGapSeconds), " 秒", null),
            ("最短片段", Number(settings.MinimumSeconds), " 秒", null),
            ("快速排除黑灯画面", "", "", settings.SkipDarkFrames)
        };
        var key = string.Join("|", rows.Select(row => row.Label + row.Value + row.Unit + row.Toggle));
        if (_settings.Tag as string == key) return;
        _settings.Tag = key; _settings.Children.Clear();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var grid = new Grid { ColumnDefinitions = new("*,Auto"), MinHeight = 30, Margin = new(14, 0) };
            grid.Children.Add(new TextBlock { Classes = { "ms-body" }, Text = Localization.Text(row.Label), VerticalAlignment = VerticalAlignment.Center });
            Control value;
            if (row.Toggle is { } on)
                value = new ToggleSwitch { Classes = { "ac", "small" }, IsChecked = on, IsHitTestVisible = false, Focusable = false, OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center };
            else
            {
                var text = new TextBlock { Classes = { "ms-caption" }, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                text.Inlines = [new Run(row.Value.Length > 0 ? row.Value : "—") { FontWeight = FontWeight.Medium, [!TextElement.ForegroundProperty] = new DynamicResourceExtension("UiText") }];
                if (row.Unit.Length > 0) text.Inlines.Add(new Run(row.Unit));
                value = text;
            }
            Grid.SetColumn(value, 1); grid.Children.Add(value);
            _settings.Children.Add(new Border { Classes = { "ms-row-line" }, BorderThickness = new(0, i == 0 ? 0 : .5, 0, 0), Child = grid });
        }
    }

    private async Task ChangeOutputAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Localization.Text("选择输出文件夹"), AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder) await _task.ChangeOutputFolderAsync(folder);
        }
        catch (Exception error) when (top is Window window) { await Ui.Message(window, "输出位置", error.Message); }
    }

    private async Task RunAsync(Func<Task>? action)
    {
        if (action is null) return;
        try { await action(); }
        catch (Exception error) when (TopLevel.GetTopLevel(this) is Window window) { await Ui.Message(window, "任务结果", error.Message); }
    }

    public Task<byte[]> ReadSegmentThumbnail(string path, ConversionOptions options, CancellationToken ct)
        => SegmentThumbnail.ReadAsync(_engine, path, options, ct, _lifetime.Token);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _previewRequest.Cancel();
        var old = _image.Source as Bitmap; _image.Source = null; old?.Dispose();
        _surface.Dispose(); _lifetime.Dispose();
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Codec(string value) => value.ToLowerInvariant() switch { "h264" => "H.264", "hevc" or "h265" => "H.265", "av1" => "AV1", "" => "", _ => value.ToUpperInvariant() };
    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    private static string Display(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}
