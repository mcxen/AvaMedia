using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Host actions for 自动字幕; null hides the action.</summary>
internal sealed record SubtitleTaskActions(Func<Task>? Review = null);

/// <summary>自动字幕 content slot: frame with the current line, recognition timeline, statistics and the transcript.</summary>
internal sealed class SubtitleStageContent : Grid, IMultiStageToolContent
{
    private sealed record LineRow(SubtitleLineView Line, bool Latest);

    private readonly ISubtitleTask _task;
    private readonly IMediaEngine _engine;
    private readonly SubtitleTaskActions _actions;
    private readonly Border _subtitle = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(16, 0, 16, 12), Padding = new(8, 3), CornerRadius = new(5), IsVisible = false };
    private readonly TextBlock _subtitleText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxLines = 2 };
    private readonly MsPreviewCard _preview;
    private readonly MsBand _band = new("识别进度");
    private readonly MsTicks _ticks = new();
    private readonly StackPanel _legend;
    private readonly TextBlock _position = MsUi.Caption();
    private readonly MsStatGrid _stats = new();
    private readonly StackPanel _runRight = new() { Spacing = 12 }, _configRight = new() { Spacing = 9 };
    private readonly MsSettingsGroup _settings = new();
    private readonly TextBlock _configNote = new() { Classes = { "ms-small" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
    private readonly Border _listCard;
    private readonly TextBlock _listTitle = new() { Classes = { "ms-heading" } }, _listNote = MsUi.Small();
    private readonly ListBox _list = new() { Classes = { "ms-segments" } };
    private readonly EmptyState _listEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ObservableCollection<LineRow> _rows = [];
    private readonly MsOutputLine _output;
    private readonly Button _reveal, _review;
    private SubtitleTaskView _view = new();
    private MultiStageTaskSnapshot _snapshot = new();
    private bool _selecting;

    public SubtitleStageContent(ISubtitleTask task, IMediaEngine engine, SubtitleTaskActions actions)
    {
        _task = task; _engine = engine; _actions = actions;
        MsUi.Brush(_subtitle, Border.BackgroundProperty, "UiMediaOverlay");
        MsUi.Brush(_subtitleText, TextBlock.ForegroundProperty, "UiMediaText");
        _subtitle.Child = _subtitleText;
        _preview = new MsPreviewCard(_subtitle);
        RowDefinitions = new("Auto,*");
        Margin = new(0, 0, 0, 12);
        MsUi.Brush(this, RowSpacingProperty, "AcMsGap");
        var left = new StackPanel { Spacing = 10 };
        MsUi.Brush(left, WidthProperty, "AcMsPreviewWidth");
        left.Children.Add(_preview); left.Children.Add(_configNote);
        _configNote.Text = Localization.Text("识别出的字幕按时间顺序逐条出现，完成后可校对再导出。");
        _legend = MsLegend.Create(("字幕", "UiAccent", false), ("无字幕", "UiTrack", false), ("未识别", "AcUnscannedFill", true));
        var timeline = new StackPanel();
        timeline.Children.Add(MsUi.HeadRow(MsUi.Heading("识别时间轴"), _legend));
        timeline.Children.Add(_band); timeline.Children.Add(_ticks);
        _position.Margin = new(0, 8, 0, 0); timeline.Children.Add(_position);
        _runRight.Children.Add(MsUi.Card(timeline)); _runRight.Children.Add(_stats);
        _configRight.Children.Add(MsUi.HeadRow(MsUi.Heading("识别设置"), null, 0)); _configRight.Children.Add(_settings);
        var right = new Panel(); right.Children.Add(_runRight); right.Children.Add(_configRight);
        Children.Add(MsUi.Columns("Auto,*", left, right));
        _listCard = BuildList(); SetRow(_listCard, 1); Children.Add(_listCard);
        _output = new MsOutputLine(task);
        _reveal = MsUi.Action("在文件夹中显示", () => { if (File.Exists(_view.Output.Path)) PlatformServices.RevealFile(_view.Output.Path); return Task.CompletedTask; });
        _review = MsUi.Action("校对字幕", actions.Review, primary: true);
        _list.SelectionChanged += (_, _) => { if (!_selecting && _list.SelectedItem is LineRow row) RefreshPreview(_view, _snapshot.State, row.Line); };
    }

    public Control Result => this;
    /// <summary>Completed subtitle-only exports still review the picture, now overlaid with the produced subtitles (UISPEC 10.17).</summary>
    public Control? ResultExtra => _resultPreview ??= new SubtitleResultView(_engine);
    private SubtitleResultView? _resultPreview;
    public Control Output => _output;
    public IReadOnlyList<Button> LeadingActions => [_reveal];
    public IReadOnlyList<Button> TrailingActions => [_review];
    public string SourceTitle => Path.GetFileName(_task.View.SourcePath);
    public string SourceTooltip => _task.View.SourcePath;
    public IReadOnlyList<string> InputPaths => _task.View.SourcePath.Length > 0 ? [_task.View.SourcePath] : [];
    // Burned-in exports list both the video and the subtitle file so the result page can review each (UISPEC 10.17).
    public IReadOnlyList<string> OutputPaths => _view.Output is { Exists: true, IsFixedLocation: false, Path.Length: > 0 } output && File.Exists(output.Path)
        ? _view.SubtitleSidecar.Length > 0 && File.Exists(_view.SubtitleSidecar) ? [output.Path, _view.SubtitleSidecar] : [output.Path]
        : [];
    public Task InitializeAsync() => Task.CompletedTask;

    private Border BuildList()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var head = MsUi.HeadRow(_listTitle, _listNote, 6); head.Margin = new(14, 11, 14, 6);
        grid.Children.Add(head);
        _list.ItemsSource = _rows;
        MsUi.ListViewport(_list);
        _list.ItemTemplate = new FuncDataTemplate<LineRow>((row, _) => row is null ? null : Row(row));
        Avalonia.Automation.AutomationProperties.SetName(_list, Localization.Text("字幕"));
        var area = new Panel(); area.Children.Add(_list); area.Children.Add(_listEmpty);
        SetRow(area, 1); grid.Children.Add(area);
        var card = MsUi.Card(grid, padded: false);
        card.VerticalAlignment = VerticalAlignment.Top;
        return card;
    }

    private static Control Row(LineRow row)
    {
        var line = row.Line;
        var grid = new Grid { ColumnDefinitions = new("30,Auto,*"), ColumnSpacing = 12, Margin = new(14, 3) };
        var number = new TextBlock { Text = line.Number.ToString(CultureInfo.InvariantCulture), FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        MsUi.Brush(number, TextBlock.ForegroundProperty, "AcTextTertiary");
        grid.Children.Add(number);
        var range = new TextBlock { Classes = { "ms-caption" }, Text = $"{PersonTaskView.Time(line.Start, 1)} — {PersonTaskView.Time(line.End, 1)}", MinWidth = 112, VerticalAlignment = VerticalAlignment.Top };
        SetColumn(range, 1); grid.Children.Add(range);
        var text = new TextBlock { Classes = { "ms-body" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        Localization.SetIsUserText(text, true);
        text.Inlines = [new Run(line.Text)];
        if (row.Latest)
        {
            var live = new Run("  " + Localization.Text("最新")) { FontSize = 10, FontWeight = FontWeight.SemiBold };
            live.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("UiAccent"));
            text.Inlines.Add(live);
        }
        SetColumn(text, 2); grid.Children.Add(text);
        return new Border { Child = grid, Classes = { "ms-row-line" }, BorderThickness = new(0, line.Number <= 1 ? 0 : .5, 0, 0) };
    }

    public MultiStageContentState Update(MultiStageTaskSnapshot snapshot)
    {
        _snapshot = snapshot;
        var view = _view = _task.View; var state = snapshot.State;
        var configuring = state == MultiStageTaskState.Configuring;
        var done = state == MultiStageTaskState.Completed;
        var running = state is MultiStageTaskState.Running or MultiStageTaskState.Paused or MultiStageTaskState.Stopping;
        _runRight.IsVisible = _listCard.IsVisible = !configuring;
        _configRight.IsVisible = _configNote.IsVisible = configuring;
        if (configuring) RefreshSettings(view);

        var duration = view.Info?.Duration ?? view.RangeEnd ?? 0;
        var partial = !done || view.IsPartial;
        var ranges = view.Lines.Select(line => (line.Start, line.End, MsBandKind.Accent)).ToList();
        _band.Update(ranges, duration, view.RecognizedSeconds, partial, state == MultiStageTaskState.Failed);
        _ticks.Update(duration);
        _legend.Children[^1].IsVisible = partial;
        var speech = view.Lines.Sum(line => Math.Max(0, line.End - line.Start));
        _position.Text = done ? (duration > 0 ? Localization.Format($"有字幕 {MsUi.Time(speech)} · 占 {speech / duration:0%}") : "")
            : state == MultiStageTaskState.Failed ? Localization.Format($"中断于 {MsUi.Time(view.RecognizedSeconds)}")
            : state == MultiStageTaskState.Cancelled ? Localization.Format($"取消于 {MsUi.Time(view.RecognizedSeconds)}")
            : view.RecognizedSeconds > 0 ? Localization.Format($"已识别至 {MsUi.Time(view.RecognizedSeconds)}") + (duration > 0 ? " / " + MsUi.Time(duration) : "")
            : Localization.Text("等待第一条字幕");

        var any = view.Lines.Count > 0 || view.RecognizedSeconds > 0;
        _stats.Set(0, done ? "字幕条数" : "已识别条数", any ? view.Lines.Count.ToString(CultureInfo.InvariantCulture) : "");
        _stats.Set(1, done ? "字幕时长" : "已识别至", done ? MsUi.Time(speech) : any ? MsUi.Time(view.RecognizedSeconds) : "",
            !done && duration > 0 && any ? "/ " + MsUi.Time(duration) : "");
        _stats.Set(2, "识别语言", Localization.Text(view.Language));
        var elapsed = snapshot.Elapsed.TotalSeconds;
        _stats.Set(3, done ? "用时" : "已用时", elapsed > 0 ? Localization.Format($"{(done ? MultiStageTaskWindow.Seconds(elapsed) : Math.Floor(elapsed).ToString(CultureInfo.InvariantCulture))} 秒") : "");

        RefreshRows(view, running, done);
        RefreshPreview(view, state, done ? (_list.SelectedItem as LineRow)?.Line : null);

        if (done) _resultPreview?.Update(view);
        _output.Update(view.Output, snapshot);
        _reveal.IsVisible = done && view.Output.Exists && File.Exists(view.Output.Path);
        _review.IsVisible = done && view.CanReview && _actions.Review is not null;
        var (errorTitle, recovery) = MsUi.Error(snapshot);
        var note = done ? Localization.Join(" · ", new[] { Localization.Format($"{view.Lines.Count} 条字幕"), view.OutputFormat,
                view.Output.Bytes is { } bytes ? MsUi.Size(bytes) : "" }.Where(s => s.Length > 0))
            : MsUi.LiveNote(snapshot, view.Lines.Count > 0);
        return new(MsUi.MediaDetail(view.Info, view.ModelName), note, MsUi.Plan(snapshot, view.ModelName), errorTitle, recovery, _review.IsVisible);
    }

    private void RefreshSettings(SubtitleTaskView view)
    {
        var range = view.RangeEnd is { } end ? $"{MsUi.Time(view.RangeStart)} — {MsUi.Time(end)}" : view.RangeStart > 0 ? $"{MsUi.Time(view.RangeStart)} — " + Localization.Text("结尾") : Localization.Text("整个视频");
        _settings.Update(
        [
            ("识别模型", view.ModelName, ""),
            ("识别语言", view.Language, ""),
            ("识别范围", range, ""),
            ("输出格式", view.OutputFormat, "")
        ]);
    }

    private void RefreshRows(SubtitleTaskView view, bool running, bool done)
    {
        _listTitle.Text = Localization.Text(done ? "字幕" : "已识别字幕");
        _listNote.Text = view.Lines.Count == 0 ? "" : done ? Localization.Format($"{view.Lines.Count} 条") + (view.CanReview ? " · " + Localization.Text("完成后可校对") : "")
            : Localization.Format($"{view.Lines.Count} 条 · ") + Localization.Text(running ? "识别中持续增加" : "部分结果");
        var rows = view.Lines.Select((line, i) => new LineRow(line, running && i == view.Lines.Count - 1)).ToArray();
        var appended = false;
        if (!rows.SequenceEqual(_rows))
        {
            _selecting = true;
            try
            {
                var common = 0;
                while (common < rows.Length && common < _rows.Count && rows[common] == _rows[common]) common++;
                while (_rows.Count > common) _rows.RemoveAt(_rows.Count - 1);
                foreach (var row in rows.Skip(common)) { _rows.Add(row); appended = true; }
            }
            finally { _selecting = false; }
        }
        // Follow the newest line while recognising; scroll after layout so the appended container exists.
        if (appended && running && _rows.Count > 0)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_rows.Count > 0) _list.ScrollIntoView(_rows.Count - 1); }, Avalonia.Threading.DispatcherPriority.Background);
        _listEmpty.IsVisible = rows.Length == 0;
        _listEmpty.Text = Localization.Text(done ? "没有识别到语音" : "尚无字幕");
        if (done && _list.SelectedItem is null && _rows.Count > 0) { _selecting = true; _list.SelectedIndex = 0; _selecting = false; }
        if (!done && _list.SelectedItem is not null) { _selecting = true; _list.SelectedItem = null; _selecting = false; }
    }

    private void RefreshPreview(SubtitleTaskView view, MultiStageTaskState state, SubtitleLineView? selected)
    {
        var stream = view.Info?.VideoStreamIndex ?? 0;
        _preview.Dimmed = state == MultiStageTaskState.Failed;
        var latest = view.Lines.LastOrDefault();
        var line = state == MultiStageTaskState.Completed ? selected ?? view.Lines.FirstOrDefault() : latest;
        _subtitle.IsVisible = line is not null && state != MultiStageTaskState.Configuring;
        _subtitleText.Text = line?.Text ?? "";
        if (state == MultiStageTaskState.Configuring || line is null)
        {
            _preview.ShowFrame(_engine, view.SourcePath, view.RangeStart, stream);
            _preview.SetChips("", view.Info is { Duration: > 0 } info ? PersonTaskView.Time(info.Duration, 0) : "");
            _preview.SetCaption("首帧", MsUi.MediaDetail(view.Info));
            return;
        }
        // Frames are fetched per line (not per progress tick) so preview work stays bounded while recognising.
        _preview.ShowFrame(_engine, view.SourcePath, (line.Start + line.End) / 2, stream);
        _preview.SetChips("");
        var title = state switch { MultiStageTaskState.Completed => "字幕预览", MultiStageTaskState.Failed => "中断前最后一条", MultiStageTaskState.Cancelled => "取消前最后一条", _ => "最新识别" };
        _preview.SetCaption(title, Localization.Format($"第 {line.Number} 条 · {PersonTaskView.Time(line.Start, 1)} — {PersonTaskView.Time(line.End, 1)}"),
            state == MultiStageTaskState.Completed ? Localization.Format($"{view.Lines.Count} 条中的第 {line.Number} 条") : "");
    }

    public void Dispose() { _preview.Dispose(); _resultPreview?.Dispose(); }

    /// <summary>Result-page preview: the source picture with a produced subtitle line overlaid at its position.</summary>
    private sealed class SubtitleResultView : Grid, IDisposable
    {
        private readonly IMediaEngine _engine;
        private readonly Border _subtitle = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new(16, 0, 16, 12), Padding = new(8, 3), CornerRadius = new(5), IsVisible = false };
        private readonly TextBlock _subtitleText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxLines = 2 };
        private readonly MsPreviewCard _preview;

        public SubtitleResultView(IMediaEngine engine)
        {
            _engine = engine;
            MsUi.Brush(_subtitle, Border.BackgroundProperty, "UiMediaOverlay");
            MsUi.Brush(_subtitleText, TextBlock.ForegroundProperty, "UiMediaText");
            _subtitle.Child = _subtitleText;
            _preview = new MsPreviewCard(_subtitle) { FollowSourceAspect = true, MaxWidth = 760 };
            Children.Add(_preview);
        }

        public void Update(SubtitleTaskView view)
        {
            var line = view.Lines.FirstOrDefault();
            if (view.Info is not { HasVideo: true } info)
            {
                _preview.IsVisible = false;
                return;
            }
            _preview.IsVisible = true;
            _subtitle.IsVisible = line is not null;
            _subtitleText.Text = line?.Text ?? "";
            _preview.ShowFrame(_engine, view.SourcePath, line is null ? view.RangeStart : (line.Start + line.End) / 2, info.VideoStreamIndex);
            _preview.SetChips("");
            _preview.SetCaption("字幕预览", line is null ? MsUi.MediaDetail(info)
                : Localization.Format($"第 {line.Number} 条 · {PersonTaskView.Time(line.Start, 1)} — {PersonTaskView.Time(line.End, 1)}"));
        }

        public void Dispose() => _preview.Dispose();
    }
}
