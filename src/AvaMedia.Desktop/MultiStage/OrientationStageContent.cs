using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Host actions for 自动旋转; null hides the action.</summary>
internal sealed record OrientationTaskActions(Func<Task>? Confirm = null, Func<Task>? OpenOutput = null);

/// <summary>自动旋转 content slot: preview with corrected orientation, decision with four-direction scores, statistics and sampled frames.</summary>
internal sealed class OrientationStageContent : Grid, IMultiStageToolContent
{
    private static readonly int[] Directions = [0, 90, 180, 270];
    private readonly IOrientationTask _task;
    private readonly IMediaEngine _engine;
    private readonly OrientationTaskActions _actions;
    private readonly MsPreviewCard _preview = new();
    private readonly TextBlock _decision = new() { Classes = { "ms-stat" }, FontSize = 20, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _reliability = MsUi.Small();
    private readonly TextBlock _reason = new() { Classes = { "ms-small" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly (TextBlock Label, ProgressBar Bar, TextBlock Value)[] _scores;
    private readonly MsStatGrid _stats = new(compact: true);
    private readonly StackPanel _runRight = new() { Spacing = 12 }, _configRight = new() { Spacing = 9 };
    private readonly MsSettingsGroup _settings = new();
    private readonly TextBlock _configNote = new() { Classes = { "ms-small" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
    private readonly Border _samplesCard;
    private readonly TextBlock _samplesTitle = new() { Classes = { "ms-heading" } }, _samplesNote = MsUi.Small();
    private readonly WrapPanel _samples = new() { Margin = new(10, 0, 10, 10) };
    private readonly EmptyState _samplesEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly MsOutputLine _output;
    private readonly Button _reveal, _confirm, _open;
    private OrientationTaskView _view = new();

    public OrientationStageContent(IOrientationTask task, IMediaEngine engine, OrientationTaskActions actions)
    {
        _task = task; _engine = engine; _actions = actions;
        RowDefinitions = new("Auto,*");
        Margin = new(0, 0, 0, 12);
        MsUi.Brush(this, RowSpacingProperty, "AcMsGap");
        var left = new StackPanel { Spacing = 10 };
        MsUi.Brush(left, WidthProperty, "AcMsPreviewWidth");
        left.Children.Add(_preview); left.Children.Add(_reason); left.Children.Add(_configNote);
        _configNote.Text = Localization.Text("开始后按采样帧逐一判定方向，结果只在全部采样完成后给出。");
        _scores = Directions.Select(_ => (MsUi.Small(), new ProgressBar { Classes = { "ac" }, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center }, MsUi.Small())).ToArray();
        _runRight.Children.Add(BuildDecision()); _runRight.Children.Add(_stats);
        _configRight.Children.Add(MsUi.HeadRow(MsUi.Heading("检测设置"), null, 0)); _configRight.Children.Add(_settings);
        var right = new Panel(); right.Children.Add(_runRight); right.Children.Add(_configRight);
        Children.Add(MsUi.Columns("Auto,*", left, right));
        _samplesCard = BuildSamples(); SetRow(_samplesCard, 1); Children.Add(_samplesCard);
        _output = new MsOutputLine(task);
        _reveal = MsUi.Action("在文件夹中显示", () => { if (File.Exists(_view.Output.Path)) PlatformServices.RevealFile(_view.Output.Path); return Task.CompletedTask; });
        _open = MsUi.Action("打开视频", actions.OpenOutput, primary: true);
        _confirm = MsUi.Action("确认并旋转…", actions.Confirm, primary: true);
    }

    public Control Result => this;
    public Control Output => _output;
    public IReadOnlyList<Button> LeadingActions => [_reveal];
    public IReadOnlyList<Button> TrailingActions => [_confirm, _open];
    public string SourceTitle => Path.GetFileName(_task.View.SourcePath);
    public string SourceTooltip => _task.View.SourcePath;
    public IReadOnlyList<string> InputPaths => _task.View.SourcePath.Length > 0 ? [_task.View.SourcePath] : [];
    public IReadOnlyList<string> OutputPaths => _view.Output is { Exists: true, IsFixedLocation: false, Path.Length: > 0 } output && File.Exists(output.Path) ? [output.Path] : [];
    public Task InitializeAsync() => Task.CompletedTask;

    private Control BuildDecision()
    {
        var stack = new StackPanel();
        stack.Children.Add(MsUi.HeadRow(MsUi.Heading("方向判定"), _reliability, 6));
        stack.Children.Add(_decision);
        var bars = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, RowSpacing = 4, Margin = new(0, 6, 0, 6) };
        for (var i = 0; i < Directions.Length; i++)
        {
            bars.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (label, bar, value) = _scores[i];
            label.Text = Direction(Directions[i]); label.MinWidth = 64;
            MsUi.Brush(bar, HeightProperty, "AcProgressHeight");
            value.MinWidth = 34; value.TextAlignment = TextAlignment.Right;
            SetRow(label, i); SetRow(bar, i); SetColumn(bar, 1); SetRow(value, i); SetColumn(value, 2);
            bars.Children.Add(label); bars.Children.Add(bar); bars.Children.Add(value);
        }
        stack.Children.Add(bars);
        return MsUi.Card(stack);
    }

    private Border BuildSamples()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var head = MsUi.HeadRow(_samplesTitle, _samplesNote, 6); head.Margin = new(14, 11, 14, 6);
        grid.Children.Add(head);
        var scroll = MsUi.Scroll(_samples);
        var area = new Panel(); area.Children.Add(scroll); area.Children.Add(_samplesEmpty);
        SetRow(area, 1); grid.Children.Add(area);
        Avalonia.Automation.AutomationProperties.SetName(_samples, Localization.Text("采样帧"));
        var card = MsUi.Card(grid, padded: false);
        card.VerticalAlignment = VerticalAlignment.Top;
        return card;
    }

    private static string Direction(int degrees) => Localization.Text(degrees switch { 0 => "保持原样", 90 => "顺时针 90°", 180 => "旋转 180°", _ => "逆时针 90°" });

    public MultiStageContentState Update(MultiStageTaskSnapshot snapshot)
    {
        var view = _view = _task.View; var state = snapshot.State;
        var configuring = state == MultiStageTaskState.Configuring;
        var done = state == MultiStageTaskState.Completed;
        _runRight.IsVisible = _samplesCard.IsVisible = !configuring;
        _configRight.IsVisible = _configNote.IsVisible = configuring;
        if (configuring) RefreshSettings(view);

        var valid = view.Samples.Where(sample => sample.Rotation is not null && sample.Scores.Count == 4).ToArray();
        var decided = done && view.HasResult && view.Decision.Length > 0;
        _decision.Text = decided ? Localization.Text(view.Decision)
            : state is MultiStageTaskState.Failed or MultiStageTaskState.Cancelled ? Localization.Text("未得出结论")
            : Localization.Text(valid.Length > 0 ? "判定中" : "等待采样");
        MsUi.Brush(_decision, TextBlock.ForegroundProperty, decided ? "UiText" : "UiTextSecondary");
        _reliability.Text = decided && view.Reliability.Length > 0 ? Localization.Format($"可信度 {Localization.Text(view.Reliability)}") : "";
        _reason.Text = decided ? view.Reason : valid.Length > 0 ? Localization.Format($"按已判定的 {valid.Length} 帧平均，最终方向在采样完成后确定。") : "";
        _reason.IsVisible = _reason.Text.Length > 0;
        for (var i = 0; i < Directions.Length; i++)
        {
            var value = valid.Length == 0 ? (double?)null : valid.Average(sample => sample.Scores[i]);
            _scores[i].Bar.Value = value ?? 0;
            _scores[i].Value.Text = value is { } v ? v.ToString("0%", CultureInfo.InvariantCulture) : "—";
            var best = decided && view.Rotation == Directions[i];
            MsUi.Brush(_scores[i].Label, TextBlock.ForegroundProperty, best ? "UiText" : "UiTextSecondary");
            _scores[i].Label.FontWeight = best ? FontWeight.SemiBold : FontWeight.Normal;
        }

        var hasFrames = view.SampledFrames > 0;
        _stats.Set(0, done ? "采样帧" : "已采样", hasFrames ? view.SampledFrames.ToString(CultureInfo.InvariantCulture) : "",
            !done && view.PlannedFrames is { } planned ? "/ " + planned.ToString(CultureInfo.InvariantCulture) : "");
        _stats.Set(1, "有效帧", hasFrames ? view.ValidFrames.ToString(CultureInfo.InvariantCulture) : "");
        _stats.Set(2, "方向一致", hasFrames && view.ValidFrames > 0 ? view.AgreeingFrames.ToString(CultureInfo.InvariantCulture) : "",
            hasFrames && view.ValidFrames > 0 ? "/ " + view.ValidFrames.ToString(CultureInfo.InvariantCulture) : "");
        var elapsed = snapshot.Elapsed.TotalSeconds;
        _stats.Set(3, done ? "用时" : "已用时", elapsed > 0 ? Localization.Format($"{(done ? MultiStageTaskWindow.Seconds(elapsed) : Math.Floor(elapsed).ToString(CultureInfo.InvariantCulture))} 秒") : "");

        RefreshSamples(view, done, state);
        RefreshPreview(view, state, decided);

        _output.Update(view.Output, snapshot, "检测结果保存在任务中");
        _reveal.IsVisible = done && view.Output.Exists && File.Exists(view.Output.Path);
        _open.IsVisible = _reveal.IsVisible && _actions.OpenOutput is not null;
        _confirm.IsVisible = done && view.CanConfirm && _actions.Confirm is not null && !_open.IsVisible;

        var (errorTitle, recovery) = MsUi.Error(snapshot);
        var note = done ? (decided ? Localization.Format($"{Localization.Text(view.Decision)} · {view.SampledFrames} 帧") : "")
            : MsUi.LiveNote(snapshot, view.Samples.Count > 0);
        return new(MsUi.MediaDetail(view.Info, view.Backend), note, MsUi.Plan(snapshot, view.Backend), errorTitle, recovery, _confirm.IsVisible || _open.IsVisible);
    }

    private void RefreshSettings(OrientationTaskView view)
    {
        var info = view.Info;
        _settings.Update(
        [
            ("检测方式", view.Backend, ""),
            ("采样帧数", view.PlannedFrames?.ToString(CultureInfo.InvariantCulture) ?? "", view.PlannedFrames is null ? "" : " 帧"),
            ("视频尺寸", info is { Width: > 0 } ? $"{info.Width}×{info.Height}" : "", ""),
            ("时长", info is { Duration: > 0 } ? MsUi.Time(info.Duration) : "", ""),
            ("判定后", Localization.Text("确认后另存为旋转后的视频"), "")
        ]);
    }

    private void RefreshSamples(OrientationTaskView view, bool done, MultiStageTaskState state)
    {
        _samplesTitle.Text = Localization.Text(done ? "采样帧" : "已判定的采样帧");
        _samplesNote.Text = view.Samples.Count == 0 ? "" : done && view.ValidFrames > 0
            ? Localization.Format($"{view.AgreeingFrames} / {view.ValidFrames} 帧方向一致")
            : Localization.Format($"{view.Samples.Count} 帧 · ") + Localization.Text(state == MultiStageTaskState.Running ? "采样中持续增加" : "部分结果");
        _samplesEmpty.IsVisible = view.Samples.Count == 0;
        _samplesEmpty.Text = Localization.Text(state is MultiStageTaskState.Failed or MultiStageTaskState.Cancelled ? "没有采样帧" : "尚无采样帧");
        var key = string.Join("|", view.Samples.Select(s => $"{s.Seconds:0.###}:{s.Rotation}:{s.Agrees}")) + (done ? "d" + view.Rotation : "");
        if (_samples.Tag as string == key) return;
        _samples.Tag = key;
        foreach (var tile in _samples.Children.OfType<SampleTile>()) tile.Dispose();
        _samples.Children.Clear();
        foreach (var sample in view.Samples) _samples.Children.Add(new SampleTile(sample, done && sample.Rotation is not null && sample.Rotation == view.Rotation));
    }

    private void RefreshPreview(OrientationTaskView view, MultiStageTaskState state, bool decided)
    {
        var latest = view.Samples.LastOrDefault(sample => sample.Image is not null);
        _preview.Dimmed = state == MultiStageTaskState.Failed;
        _preview.Rotation = 0; _preview.SetChips("");
        switch (state)
        {
            case MultiStageTaskState.Configuring:
                _preview.ShowFrame(_engine, view.SourcePath, 0, view.Info?.VideoStreamIndex ?? 0);
                _preview.SetChips("", view.Info is { Duration: > 0 } info ? PersonTaskView.Time(info.Duration, 0) : "");
                _preview.SetCaption("首帧", MsUi.MediaDetail(view.Info));
                break;
            case MultiStageTaskState.Completed when decided:
                var shown = view.Samples.Where(s => s.Image is not null && s.Agrees).Skip(view.Samples.Count(s => s.Image is not null && s.Agrees) / 2).FirstOrDefault() ?? latest;
                if (shown is not null) _preview.ShowEncoded(shown.Image); else _preview.ShowFrame(_engine, view.SourcePath, 0, view.Info?.VideoStreamIndex ?? 0);
                _preview.Rotation = view.Rotation ?? 0;
                _preview.SetChips(shown is null ? "" : PersonTaskView.Time(shown.Seconds, 1));
                _preview.SetCaption(view.Rotation is > 0 ? "校正后预览" : "原始画面", Localization.Text(view.Decision));
                break;
            default:
                if (latest is not null) _preview.ShowEncoded(latest.Image); else _preview.ShowFrame(_engine, view.SourcePath, 0, view.Info?.VideoStreamIndex ?? 0);
                if (latest is not null) _preview.SetChips(PersonTaskView.Time(latest.Seconds, 1) + " · " + (latest.Rotation is { } r ? Direction(r) : Localization.Text("无法判定")));
                var title = state switch { MultiStageTaskState.Failed => "中断处画面", MultiStageTaskState.Cancelled => "取消时画面", MultiStageTaskState.Completed => "最后采样帧", _ => "当前采样帧" };
                _preview.SetCaption(title, Localization.Format($"已采样 {view.SampledFrames} 帧"));
                break;
        }
    }

    public void Dispose()
    {
        foreach (var tile in _samples.Children.OfType<SampleTile>()) tile.Dispose();
        _preview.Dispose();
    }

    /// <summary>One sampled frame: thumbnail, timestamp and detected direction; agreeing frames carry an accent outline once decided.</summary>
    private sealed class SampleTile : StackPanel, IDisposable
    {
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
        private bool _disposed;
        public SampleTile(OrientationSampleView sample, bool agrees)
        {
            Width = 112; Margin = new(4); Spacing = 4;
            var frame = new Border { Height = 63, CornerRadius = new(6), ClipToBounds = true, Child = _image };
            MsUi.Brush(frame, Border.BackgroundProperty, "AcPreviewFill");
            if (agrees) { frame.BorderThickness = new(2); MsUi.Brush(frame, Border.BorderBrushProperty, "UiAccent"); }
            var holder = new Grid(); holder.Children.Add(frame);
            if (sample.Rotation is { } rotation && rotation != 0)
                holder.Children.Add(new PreviewChip(HorizontalAlignment.Right) { Text = $"+{rotation}°", Margin = new(4) });
            Children.Add(holder);
            var caption = new TextBlock { Classes = { "ms-small" }, TextTrimming = TextTrimming.CharacterEllipsis };
            caption.Inlines = [new Run(PersonTaskView.Time(sample.Seconds, 1)) { [!TextElement.ForegroundProperty] = new DynamicResourceExtension("UiText") },
                new Run("  " + (sample.Rotation is { } d ? Direction(d) : Localization.Text("无法判定")))];
            Children.Add(caption);
            ToolTip.SetTip(this, sample.Scores.Count == 4
                ? string.Join("  ", Directions.Select((deg, i) => $"{Direction(deg)} {sample.Scores[i]:0%}")) : null);
            if (sample.Image is { } bytes) _ = LoadAsync(bytes);
        }
        private async Task LoadAsync(byte[] bytes)
        {
            try
            {
                var bitmap = await Task.Run(() => { using var memory = new MemoryStream(bytes); return Bitmap.DecodeToWidth(memory, 224); });
                if (_disposed) { bitmap.Dispose(); return; }
                _image.Source = bitmap;
            }
            catch (Exception error) { AppDiagnostics.Record("Orientation sample", error); }
        }
        public void Dispose() { _disposed = true; var old = _image.Source as Bitmap; _image.Source = null; old?.Dispose(); }
    }
}
