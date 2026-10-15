using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Host actions for 视频总结; null hides the action.</summary>
internal sealed record VideoSummaryTaskActions(Func<Task>? OpenReport = null);

/// <summary>视频总结 content slot: frame, segment understanding progress, statistics and the summary / outline result.</summary>
internal sealed class VideoSummaryStageContent : Grid, IMultiStageToolContent
{
    private readonly IVideoSummaryTask _task;
    private readonly IMediaEngine _engine;
    private readonly VideoSummaryTaskActions _actions;
    private readonly MsPreviewCard _preview = new();
    private readonly MsBand _band = new("分段理解进度");
    private readonly MsTicks _ticks = new();
    private readonly StackPanel _bandArea = new();
    private readonly StackPanel _legend;
    private readonly TextBlock _segmentNote = new() { Classes = { "ms-caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly MsStatGrid _stats = new();
    private readonly StackPanel _runRight = new() { Spacing = 12 }, _configRight = new() { Spacing = 9 };
    private readonly MsSettingsGroup _settings = new();
    private readonly TextBlock _configNote = new() { Classes = { "ms-small" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
    private readonly Border _resultCard;
    private readonly TextBlock _resultTitle = new() { Classes = { "ms-heading" } }, _resultNote = MsUi.Small();
    private readonly StackPanel _result = new() { Spacing = 14, Margin = new(14, 2, 14, 14) };
    private readonly EmptyState _resultEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly MsOutputLine _output;
    private readonly Button _reveal, _open;
    private VideoSummaryTaskView _view = new();
    private string _resultKey = "";

    public VideoSummaryStageContent(IVideoSummaryTask task, IMediaEngine engine, VideoSummaryTaskActions actions)
    {
        _task = task; _engine = engine; _actions = actions;
        RowDefinitions = new("Auto,*");
        MsUi.Brush(this, RowSpacingProperty, "AcMsGap");
        var left = new StackPanel { Spacing = 10 };
        MsUi.Brush(left, WidthProperty, "AcMsPreviewWidth");
        left.Children.Add(_preview); left.Children.Add(_configNote);
        _configNote.Text = Localization.Text("长视频先按内容分段理解，再汇总成章节与概要；中断后可从分段理解继续。");
        _legend = MsLegend.Create(("已理解", "UiAccent", false), ("理解中", "AcSelection", false), ("待处理", "UiTrack", false));
        _bandArea.Children.Add(_band); _bandArea.Children.Add(_ticks);
        var progress = new StackPanel();
        progress.Children.Add(MsUi.HeadRow(MsUi.Heading("分段理解"), _legend));
        progress.Children.Add(_bandArea);
        _segmentNote.Margin = new(0, 8, 0, 0); progress.Children.Add(_segmentNote);
        _runRight.Children.Add(MsUi.Card(progress)); _runRight.Children.Add(_stats);
        _configRight.Children.Add(MsUi.HeadRow(MsUi.Heading("总结设置"), null, 0)); _configRight.Children.Add(_settings);
        var right = new Panel(); right.Children.Add(_runRight); right.Children.Add(_configRight);
        Children.Add(MsUi.Columns("Auto,*", left, right));
        _resultCard = BuildResult(); SetRow(_resultCard, 1); Children.Add(_resultCard);
        _output = new MsOutputLine(task);
        _reveal = MsUi.Action("在文件夹中显示", () => { if (File.Exists(_view.Output.Path)) PlatformServices.RevealFile(_view.Output.Path); return Task.CompletedTask; });
        _open = MsUi.Action("查看完整报告", actions.OpenReport, primary: true);
    }

    public Control Result => this;
    public Control Output => _output;
    public IReadOnlyList<Button> LeadingActions => [_reveal];
    public IReadOnlyList<Button> TrailingActions => [_open];
    public string SourceTitle => Path.GetFileName(_task.View.SourcePath);
    public string SourceTooltip => _task.View.SourcePath;
    public Task InitializeAsync() => Task.CompletedTask;

    private Border BuildResult()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var head = MsUi.HeadRow(_resultTitle, _resultNote, 6); head.Margin = new(14, 11, 14, 6);
        grid.Children.Add(head);
        var scroll = new ScrollViewer { Content = _result, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var area = new Panel(); area.Children.Add(scroll); area.Children.Add(_resultEmpty);
        SetRow(area, 1); grid.Children.Add(area);
        return MsUi.Card(grid, padded: false);
    }

    public MultiStageContentState Update(MultiStageTaskSnapshot snapshot)
    {
        var view = _view = _task.View; var state = snapshot.State;
        var configuring = state == MultiStageTaskState.Configuring;
        var done = state == MultiStageTaskState.Completed;
        _runRight.IsVisible = _resultCard.IsVisible = !configuring;
        _configRight.IsVisible = _configNote.IsVisible = configuring;
        if (configuring) RefreshSettings(view);

        var duration = view.Info?.Duration ?? 0;
        var segments = view.Segments;
        var understood = segments.Count(s => s.State is StageState.Done or StageState.Skipped);
        _bandArea.IsVisible = segments.Count > 0;
        _legend.IsVisible = segments.Count > 0 && understood < segments.Count;
        if (segments.Count > 0)
        {
            var ranges = segments.Select(s => (s.Start, s.End, s.State switch
            {
                StageState.Done or StageState.Skipped => MsBandKind.Accent, StageState.Active or StageState.Paused => MsBandKind.Soft,
                StageState.Failed => MsBandKind.Danger, _ => MsBandKind.Track
            })).ToList();
            _band.Update(ranges, duration, duration, false, false);
            _ticks.Update(duration);
        }
        var active = segments.FirstOrDefault(s => s.State is StageState.Active or StageState.Paused);
        _segmentNote.Text = segments.Count > 0
            ? Localization.Format($"{understood} / {segments.Count} 段已理解") + (active is null ? "" : " · " + Localization.Format($"正在理解第 {active.Number} 段 {MsUi.Time(active.Start)} — {MsUi.Time(active.End)}"))
            : view.IsLongVideo ? Localization.Text("分段规划完成后显示各段进度")
            : Localization.Text("短视频整体理解，不分段");

        _stats.Set(0, "分析画面", view.FramesAnalyzed > 0 ? view.FramesAnalyzed.ToString(CultureInfo.InvariantCulture) : "", view.FramesAnalyzed > 0 ? Localization.Text("帧") : "");
        _stats.Set(1, "字幕/转写", view.TranscriptLines > 0 ? view.TranscriptLines.ToString(CultureInfo.InvariantCulture) : view.TranscriptSource.Length > 0 ? Localization.Text(view.TranscriptSource) : "",
            view.TranscriptLines > 0 ? Localization.Text("条") + (view.TranscriptSource.Length > 0 ? " · " + Localization.Text(view.TranscriptSource) : "") : "");
        _stats.Set(2, done ? "章节" : "已理解分段", done ? (view.Chapters.Count > 0 ? view.Chapters.Count.ToString(CultureInfo.InvariantCulture) : "") : segments.Count > 0 ? understood.ToString(CultureInfo.InvariantCulture) : "",
            !done && segments.Count > 0 ? "/ " + segments.Count.ToString(CultureInfo.InvariantCulture) : "");
        var elapsed = snapshot.Elapsed.TotalSeconds;
        _stats.Set(3, done ? "用时" : "已用时", elapsed > 0 ? Localization.Format($"{(done ? MultiStageTaskWindow.Seconds(elapsed) : Math.Floor(elapsed).ToString(CultureInfo.InvariantCulture))} 秒") : "");

        RefreshResult(view, state);
        RefreshPreview(view, state, active);

        _output.Update(view.Output, snapshot, "总结保存在任务中");
        _reveal.IsVisible = done && view.Output.Exists && File.Exists(view.Output.Path);
        _open.IsVisible = done && view.HasResult && _actions.OpenReport is not null;
        var (errorTitle, recovery) = MsUi.Error(snapshot);
        var note = done ? Localization.Join(" · ", new[] { view.Chapters.Count > 0 ? Localization.Format($"{view.Chapters.Count} 个章节") : "",
                view.Highlights.Count > 0 ? Localization.Format($"{view.Highlights.Count} 条要点") : "" }.Where(s => s.Length > 0))
            : MsUi.LiveNote(snapshot, view.Summary.Length > 0 || understood > 0);
        return new(MsUi.MediaDetail(view.Info, view.IsLongVideo ? Localization.Text("长视频分段") : ""), note, MsUi.Plan(snapshot, view.ModelName), errorTitle, recovery, _open.IsVisible);
    }

    private void RefreshSettings(VideoSummaryTaskView view)
    {
        var info = view.Info;
        _settings.Update(
        [
            ("理解模型", view.ModelName, ""),
            ("时长", info is { Duration: > 0 } ? MsUi.Time(info.Duration) : "", ""),
            ("处理方式", view.IsLongVideo ? "分段理解后汇总" : "整体理解", ""),
            ("字幕来源", view.TranscriptSource, "")
        ]);
    }

    private void RefreshResult(VideoSummaryTaskView view, MultiStageTaskState state)
    {
        var done = state == MultiStageTaskState.Completed;
        var showSummary = view.Summary.Length > 0 || view.Chapters.Count > 0 || view.Highlights.Count > 0;
        _resultTitle.Text = Localization.Text(showSummary ? (done && !view.IsPartial ? "总结" : "部分总结") : "分段内容");
        _resultNote.Text = showSummary ? string.Join(" · ", view.Keywords.Take(6))
            : view.Segments.Count > 0 ? Localization.Format($"{view.Segments.Count} 段") : "";
        Localization.SetIsUserText(_resultNote, showSummary);
        var key = showSummary
            ? "s:" + view.Summary + "|" + string.Join("|", view.Highlights) + "|" + string.Join("|", view.Chapters.Select(c => $"{c.Start}:{c.Title}:{c.Text}"))
            : "g:" + string.Join("|", view.Segments.Select(s => $"{s.Number}:{s.State}:{s.Title}"));
        _resultEmpty.IsVisible = !showSummary && view.Segments.Count == 0;
        _resultEmpty.Text = Localization.Text(state is MultiStageTaskState.Failed or MultiStageTaskState.Cancelled ? "没有可查看的总结" : "总结在理解完成后生成");
        if (_resultKey == key) return;
        _resultKey = key; _result.Children.Clear();
        if (!showSummary) { foreach (var segment in view.Segments) _result.Children.Add(SegmentRow(segment)); return; }
        if (view.Summary.Length > 0) _result.Children.Add(Paragraph(view.Summary));
        if (view.Highlights.Count > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            list.Children.Add(SubHeading("要点"));
            foreach (var item in view.Highlights) list.Children.Add(Bullet(item));
            _result.Children.Add(list);
        }
        if (view.Chapters.Count > 0)
        {
            var list = new StackPanel { Spacing = 0 };
            list.Children.Add(SubHeading("章节"));
            for (var i = 0; i < view.Chapters.Count; i++) list.Children.Add(ChapterRow(view.Chapters[i], i == 0));
            _result.Children.Add(list);
        }
    }

    private static TextBlock SubHeading(string text) => new() { Classes = { "ms-caption" }, Text = Localization.Text(text), FontWeight = FontWeight.SemiBold, Margin = new(0, 0, 0, 4) };
    private static TextBlock Paragraph(string text)
    {
        var block = new TextBlock { Classes = { "ms-body" }, Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        Localization.SetIsUserText(block, true); return block;
    }
    private static Control Bullet(string text)
    {
        var grid = new Grid { ColumnDefinitions = new("14,*") };
        var dot = new Border { Width = 5, Height = 5, CornerRadius = new(2.5), VerticalAlignment = VerticalAlignment.Top, Margin = new(2, 8, 0, 0) };
        MsUi.Brush(dot, Border.BackgroundProperty, "UiAccent");
        grid.Children.Add(dot);
        var body = Paragraph(text); SetColumn(body, 1); grid.Children.Add(body);
        return grid;
    }
    private static Control ChapterRow(VideoSummaryChapterView chapter, bool first)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 14, Margin = new(0, 8) };
        var time = new TextBlock { Classes = { "ms-caption" }, Text = PersonTaskView.Time(chapter.Start, 0), MinWidth = 52, VerticalAlignment = VerticalAlignment.Top };
        MsUi.Brush(time, TextBlock.ForegroundProperty, "UiAccent");
        grid.Children.Add(time);
        var body = new StackPanel { Spacing = 3 };
        var title = new TextBlock { Classes = { "ms-body" }, Text = chapter.Title, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap };
        Localization.SetIsUserText(title, true); body.Children.Add(title);
        if (chapter.Text.Length > 0)
        {
            var text = new TextBlock { Classes = { "ms-caption" }, Text = chapter.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            Localization.SetIsUserText(text, true); body.Children.Add(text);
        }
        SetColumn(body, 1); grid.Children.Add(body);
        return new Border { Classes = { "ms-row-line" }, BorderThickness = new(0, first ? 0 : .5, 0, 0), Child = grid };
    }
    private static Control SegmentRow(VideoSummarySegmentView segment)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 12, Margin = new(0, 7) };
        var glyph = new StageGlyph { State = MsUi.GlyphName(segment.State), Number = segment.Number, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(glyph);
        var range = new TextBlock { Classes = { "ms-caption" }, Text = $"{PersonTaskView.Time(segment.Start, 0)} — {PersonTaskView.Time(segment.End, 0)}", MinWidth = 104, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(range, 1); grid.Children.Add(range);
        var title = new TextBlock { Classes = { "ms-body" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines = [new Run(segment.Title.Length > 0 ? segment.Title : Localization.Text(segment.State switch
        {
            StageState.Active => "理解中", StageState.Paused => "已暂停", StageState.Failed => "理解失败", StageState.Cancelled => "已取消", StageState.Done or StageState.Skipped => "已理解", _ => "等待理解"
        }))];
        if (segment.Title.Length == 0) MsUi.Brush(title, TextBlock.ForegroundProperty, "UiTextSecondary");
        else Localization.SetIsUserText(title, true);
        SetColumn(title, 2); grid.Children.Add(title);
        return new Border { Classes = { "ms-row-line" }, BorderThickness = new(0, segment.Number <= 1 ? 0 : .5, 0, 0), Child = grid };
    }

    private void RefreshPreview(VideoSummaryTaskView view, MultiStageTaskState state, VideoSummarySegmentView? active)
    {
        var stream = view.Info?.VideoStreamIndex ?? 0;
        _preview.Dimmed = state == MultiStageTaskState.Failed;
        var duration = view.Info?.Duration ?? 0;
        if (active is not null && state != MultiStageTaskState.Configuring)
        {
            _preview.ShowFrame(_engine, view.SourcePath, (active.Start + active.End) / 2, stream);
            _preview.SetChips(Localization.Format($"第 {active.Number} 段 · {PersonTaskView.Time(active.Start, 0)} — {PersonTaskView.Time(active.End, 0)}"));
            _preview.SetCaption(state == MultiStageTaskState.Failed ? "中断处分段" : "当前分段", active.Title, Localization.Format($"共 {view.Segments.Count} 段"));
            return;
        }
        var at = duration > 0 && state != MultiStageTaskState.Configuring ? duration * .1 : 0;
        _preview.ShowFrame(_engine, view.SourcePath, at, stream);
        _preview.SetChips("", duration > 0 ? PersonTaskView.Time(duration, 0) : "");
        _preview.SetCaption(state == MultiStageTaskState.Configuring ? "首帧" : "视频画面", MsUi.MediaDetail(view.Info));
    }

    public void Dispose() => _preview.Dispose();
}
