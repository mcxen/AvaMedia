using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>Person task observations and review, with virtualized, cancellable frame requests.</summary>
internal sealed class PersonTaskView : Grid, ISegmentThumbnailSource, IDisposable
{
    protected override Type StyleKeyOverride => typeof(Grid);
    private readonly IMediaEngine _engine;
    private readonly Job _job;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<ClipSegmentEntry> _segments = [];
    private readonly ListBox _list = new(), _strip = new();
    private readonly StatusPill _status = new();
    private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
    private readonly MediaPreviewSurface _surface = new() { FitContent = false, UseAmbientLighting = false };
    private readonly TextBlock _placeholder = Label("暂无预览", true), _metadata = Label("", true), _duration = Label("", true);
    private readonly TextBlock _count = Label("—"), _kept = Label("—"), _elapsed = Label("—"), _frames = Label("—");
    private readonly TextBlock _retention = Label("", true), _progressText = Label("", true), _recordSummary = Label("", true);
    private readonly TextBlock _empty = Label("等待检测结果", true), _scanText = Label("", true);
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Maximum = 100 };
    private readonly StackPanel _progressPanel = new() { Spacing = 8 };
    private readonly PersonTaskTimeline _timeline = new();
    private readonly Expander _records;
    private readonly TextBlock _error;
    private readonly PreviewRequest _previewRequest = new();
    private readonly TextBlock[] _ticks = Enumerable.Range(0, 5).Select(_ => Label("", true)).ToArray();
    private PersonClipResult? _observed;
    private MediaInfo? _info;
    private bool _disposed, _selecting;
    private int _loadRevision;
    private JobState? _loadState;
    private double? _loggedElapsed;

    public bool HasCompleteResult => _observed is { IsPartial: false, Segments.Count: > 0 };

    public PersonTaskView(IMediaEngine engine, Job job, Expander records, TextBlock error)
    {
        _engine = engine; _job = job; _records = records; _error = error;
        Classes.Add("person-task-view");
        Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop/")) { Source = new("avares://AvaMedia.Desktop/Styles/PersonTaskStyles.axaml") });
        ColumnDefinitions = new("42*,58*"); ColumnSpacing = 12;
        var left = new StackPanel { Spacing = 12 };
        Children.Add(left);
        left.Children.Add(BuildSource()); left.Children.Add(BuildStats());
        var right = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        Grid.SetColumn(right, 1); Children.Add(right);
        right.Children.Add(BuildTimeline());
        var result = BuildSegments(); Grid.SetRow(result, 1); right.Children.Add(result);
        _timeline.SelectSegment += index => Select(index, true);
        _list.SelectionChanged += (_, _) => { if (!_selecting) Select(_list.SelectedIndex, false); };
        _strip.SelectionChanged += (_, _) => { if (!_selecting) Select(_strip.SelectedIndex, true); };
        _records.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty && _records.IsExpanded) _ = ReadSummaryAsync(); };
        _strip.IsVisible = false; UpdateMetadata(); Update();
    }

    private Control BuildSource()
    {
        var card = new Card { Classes = { "person-source" } };
        var contents = new StackPanel { Spacing = 10 };
        var frame = new Grid { Classes = { "person-preview" } };
        Resource(frame, BackgroundProperty, "AcPreviewFill");
        frame.Children.Add(_preview);
        _placeholder.HorizontalAlignment = HorizontalAlignment.Center; _placeholder.VerticalAlignment = VerticalAlignment.Center;
        frame.Children.Add(_placeholder);
        var durationFace = new Border { Child = _duration, Classes = { "person-duration" }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        frame.Children.Add(durationFace); _surface.PreviewContent = frame;
        contents.Children.Add(_surface);
        var name = Label(string.Join(" · ", _job.Inputs.Select(Path.GetFileName)));
        name.Classes.Add("person-name"); name.TextTrimming = TextTrimming.CharacterEllipsis;
        Localization.SetIsUserText(name, true); ToolTip.SetTip(name, _job.Source); contents.Children.Add(name);
        var meta = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        _metadata.TextWrapping = TextWrapping.Wrap; meta.Children.Add(_metadata);
        _status.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(_status, 1); meta.Children.Add(_status);
        contents.Children.Add(meta); card.Child = contents; return card;
    }

    private Control BuildStats()
    {
        var card = new Card { Classes = { "person-stats" } };
        var cells = new Grid { RowDefinitions = new("Auto,Auto"), ColumnDefinitions = new("*,*") };
        var labels = new[] { "保留片段", "保留时长", "用时", "检测帧数" };
        var values = new[] { _count, _kept, _elapsed, _frames };
        for (var i = 0; i < 4; i++)
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(Label(labels[i], true)); values[i].Classes.Add("person-stat-number"); stack.Children.Add(values[i]);
            var cell = new Border { Child = stack, Classes = { "person-stat-cell" }, BorderThickness = new(i % 2 == 1 ? .5 : 0, i >= 2 ? .5 : 0, 0, 0) };
            Grid.SetRow(cell, i / 2); Grid.SetColumn(cell, i % 2); cells.Children.Add(cell);
        }
        card.Child = cells; return card;
    }

    private Control BuildTimeline()
    {
        var card = new Card(); var contents = new StackPanel { Spacing = 10 };
        _error.TextWrapping = TextWrapping.Wrap; contents.Children.Add(_error);
        var progressHead = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        progressHead.Children.Add(Heading("人物检测"));
        _progressText.HorizontalAlignment = HorizontalAlignment.Right; _progressText.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_progressText, 1); progressHead.Children.Add(_progressText);
        _progressPanel.Children.Add(progressHead); _progressPanel.Children.Add(_progress); contents.Children.Add(_progressPanel);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        heading.Children.Add(Heading("人物出现时间轴"));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        legend.Children.Add(Legend("有人 · 保留", "UiAccent")); legend.Children.Add(Legend("无人 · 移除", "UiTrack"));
        Grid.SetColumn(legend, 1); heading.Children.Add(legend); contents.Children.Add(heading);
        contents.Children.Add(_timeline);
        var ticks = new Grid { ColumnDefinitions = new("*,*,*,*,*") };
        for (var i = 0; i < 5; i++)
        {
            _ticks[i].Classes.Add("person-tick"); _ticks[i].HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            Grid.SetColumn(_ticks[i], i); ticks.Children.Add(_ticks[i]);
        }
        contents.Children.Add(ticks);
        _scanText.TextWrapping = TextWrapping.Wrap; contents.Children.Add(_scanText);
        ConfigureList(_strip, true); contents.Children.Add(_strip);
        card.Child = contents; return card;
    }

    private Control BuildSegments()
    {
        var card = new Card { Classes = { "person-result" } };
        var contents = new Grid { RowDefinitions = new("Auto,*,Auto") };
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Classes = { "person-result-header" } };
        heading.Children.Add(Heading("保留片段"));
        _retention.HorizontalAlignment = HorizontalAlignment.Right; _retention.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_retention, 1); heading.Children.Add(_retention); contents.Children.Add(heading);
        var listArea = new Grid(); ConfigureList(_list, false); listArea.Children.Add(_list);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; listArea.Children.Add(_empty);
        Grid.SetRow(listArea, 1); contents.Children.Add(listArea);
        var header = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        header.Children.Add(Label("处理记录", true)); Grid.SetColumn(_recordSummary, 1);
        _recordSummary.HorizontalAlignment = HorizontalAlignment.Right; _recordSummary.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_recordSummary); _records.Header = header; _records.Classes.Add("person-records");
        Grid.SetRow(_records, 2); contents.Children.Add(_records);
        card.Child = contents; return card;
    }

    private void ConfigureList(ListBox list, bool strip)
    {
        list.ItemsSource = _segments; list.Classes.Add(strip ? "person-strip" : "person-segments");
        list.ItemTemplate = new FuncDataTemplate<ClipSegmentEntry>((entry, _) =>
        {
            var image = new PersonTaskThumbnail { Stretch = Stretch.UniformToFill };
            var face = new Border { Child = image, Classes = { "person-thumb" } };
            if (strip) { face.Classes.Add("strip-thumb"); ToolTip.SetTip(face, entry?.Summary); return face; }
            var row = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 12, Classes = { "person-segment-row" } };
            var number = Label("", true); number.Classes.Add("person-number"); number.Bind(TextBlock.TextProperty, new Binding(nameof(ClipSegmentEntry.Number))); row.Children.Add(number);
            Grid.SetColumn(face, 1); row.Children.Add(face);
            var range = Label(""); range.Bind(TextBlock.TextProperty, new Binding(nameof(ClipSegmentEntry.ShortRange)));
            Grid.SetColumn(range, 2); row.Children.Add(range);
            var duration = Label("", true); duration.Bind(TextBlock.TextProperty, new Binding(nameof(ClipSegmentEntry.ShortDuration)));
            Grid.SetColumn(duration, 3); row.Children.Add(duration);
            var separator = new Border { Child = row, BorderThickness = new(0, 0, 0, .5) };
            Resource(separator, Border.BorderBrushProperty, "UiDivider"); return separator;
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, strip ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, strip ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        if (strip) list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { Orientation = Orientation.Horizontal });
    }

    private void Select(int index, bool scrollList)
    {
        if (_selecting || index < 0 || index >= _segments.Count) return;
        _selecting = true;
        _list.SelectedIndex = _strip.SelectedIndex = index; _timeline.Selected = index;
        if (scrollList) _list.ScrollIntoView(index);
        _strip.ScrollIntoView(index); _selecting = false;
        var segment = _segments[index]; _ = LoadPreviewAsync(segment.Options.Start, segment.Options.VideoStreamIndex);
    }

    public async Task InitializeAsync()
    {
        await LoadStoredResultAsync();
        if (_disposed) return;
        if (_info is null && _job.Inputs.FirstOrDefault() is { } path)
        {
            try { _info = await Task.Run(() => _engine.Probe(path, _lifetime.Token), _lifetime.Token); UpdateMetadata(); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* Metadata and images are optional; the task result remains readable. */ }
        }
        if (!_disposed && _preview.Source is null) await LoadPreviewAsync(0, _info?.VideoStreamIndex ?? 0);
        await ReadSummaryAsync();
    }

    private async Task LoadStoredResultAsync()
    {
        var revision = ++_loadRevision; var state = _job.State;
        if (_job.PersonDetectionResult is not null || state is JobState.Running or JobState.Stopping or JobState.Waiting) return;
        try
        {
            var stored = await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(_job.Options.PersonClip?.AnalysisOnly == true ? _job.Output : AiTaskResults.PathFor(_job, "people"), _lifetime.Token);
            if (_disposed || revision != _loadRevision || state != _job.State || _job.PersonDetectionResult is not null) return;
            if (stored is not null) { _job.PersonDetectionResult = stored; Update(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppDiagnostics.Record("Person task result", error); }
    }

    private async Task ReadSummaryAsync()
    {
        var revision = _loadRevision;
        try
        {
            var log = await _job.ReadLogAsync();
            if (_disposed || revision != _loadRevision) return;
            _loggedElapsed = null;
            const string prefix = "人物检测耗时：";
            foreach (var line in log.Split('\n'))
            {
                var start = line.IndexOf(prefix, StringComparison.Ordinal);
                if (start < 0) continue;
                var value = line[(start + prefix.Length)..].Trim().Split(' ')[0];
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0) _loggedElapsed = seconds;
            }
            UpdateNumbers();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppDiagnostics.Record("Person task log summary", error); }
    }

    public void Update()
    {
        if (_disposed) return;
        var state = _job.State;
        _status.State = state switch { JobState.Running => "running", JobState.Completed => "completed", JobState.Failed => "failed", JobState.Paused => "paused", _ => "waiting" };
        _status.Text = state == JobState.Running ? Localization.Format($"进行中 {_job.Progress:0}%") : Localization.Text(state switch
        { JobState.Completed => "完成", JobState.Failed => "失败", JobState.Waiting => "等待中", JobState.Paused => "已暂停", JobState.Stopping => "正在终止", _ => "已停止" });
        if (_loadState != state)
        {
            _loadState = state; ++_loadRevision; _loggedElapsed = null;
            if (state is JobState.Running or JobState.Waiting) { _previewRequest.Cancel(); _ = LoadPreviewAsync(0, _info?.VideoStreamIndex ?? 0); }
            else { _ = LoadStoredResultAsync(); _ = ReadSummaryAsync(); }
        }
        var result = _job.PersonDetectionResult?.Result;
        if (!ReferenceEquals(_observed, result))
        {
            _observed = result;
            if (result is not null) _info = result.Info;
            var count = result?.Segments.Count ?? 0;
            while (_segments.Count > count) _segments.RemoveAt(_segments.Count - 1);
            for (var i = 0; i < count; i++)
            {
                var range = result!.Segments[i].Clone(); range.VideoStreamIndex = result.Info.VideoStreamIndex;
                if (i < _segments.Count)
                {
                    if (_segments[i].Options.Start != range.Start || _segments[i].End != range.End || _segments[i].Options.VideoStreamIndex != range.VideoStreamIndex) _segments[i].Options = range;
                }
                else _segments.Add(new(range, result.Path) { Number = i + 1, SourceDuration = result.Info.Duration });
            }
            _timeline.Update(result, _info?.Duration ?? _job.Duration);
            _empty.IsVisible = count == 0;
            _empty.Text = Localization.Text(result is { IsPartial: false } ? "没有可保留片段" : "等待检测结果");
            _strip.IsVisible = count > 0;
            if (count > 0 && _list.SelectedIndex < 0) Select(0, false);
            UpdateMetadata();
        }
        _timeline.ShowCursor = result?.IsPartial == true;
        _progressPanel.IsVisible = state is JobState.Waiting or JobState.Running or JobState.Paused or JobState.Stopping;
        _progress.Value = double.IsFinite(_job.Progress) ? Math.Clamp(_job.Progress, 0, 100) : 0;
        _progress.IsIndeterminate = state == JobState.Waiting || state == JobState.Running && _job.Progress <= 0;
        _progressText.Text = Localization.Join(" · ", new[] { _job.ProgressDetail, result is null ? "" : Localization.Format($"已检测 {result.SampledFrames} 帧"), _job.RemainingTimeText }.Where(s => s.Length > 0));
        _scanText.IsVisible = result?.IsPartial == true;
        _scanText.Text = result?.IsPartial == true ? Localization.Format($"已检测至 {Time(result.AnalyzedSeconds, 1)} · 其后尚未检测") : "";
        if (state == JobState.Failed && result?.IsPartial == true && _error.IsVisible && _error.Text == _job.Error)
            _error.Text = Localization.Format($"检测在 {Time(result.AnalyzedSeconds, 1)} 处中断：{_job.Error}");
        UpdateNumbers();
    }

    private void UpdateMetadata()
    {
        var info = _info;
        _metadata.Text = info is null ? "—" : string.Join(" · ", new[] { info.Width > 0 && info.Height > 0 ? $"{info.Width}×{info.Height}" : "", Codec(info.VideoCodec), info.Duration > 0 ? Time(info.Duration, 1) : "" }.Where(s => s.Length > 0));
        _duration.Text = info?.Duration > 0 ? Time(info.Duration, 0) : "";
        _duration.IsVisible = _duration.Text.Length > 0;
        _timeline.Update(_observed, info?.Duration ?? _job.Duration);
        for (var i = 0; i < 5; i++) _ticks[i].Text = info?.Duration > 0 ? Time(info.Duration * i / 4, 0) : "—";
    }

    private void UpdateNumbers()
    {
        var result = _observed;
        var kept = result?.Segments.Sum(s => Math.Max(0, s.End - s.Start)) ?? 0;
        _count.Text = result is null ? "—" : result.Segments.Count.ToString(CultureInfo.InvariantCulture);
        _kept.Text = result is null ? "—" : Time(kept, 1);
        _frames.Text = result is null ? "—" : result.SampledFrames.ToString(CultureInfo.InvariantCulture);
        var activity = _job.Activity;
        var elapsed = _loggedElapsed ?? (activity is null ? (double?)null : Math.Max(0, ((_job.State == JobState.Running ? DateTime.UtcNow : activity.UpdatedUtc) - activity.StartedUtc).TotalSeconds));
        _elapsed.Text = elapsed is null ? "—" : Localization.Format($"{elapsed.Value:0.#} 秒");
        _retention.Text = result is null || result.Info.Duration <= 0 ? "" : result.IsPartial
            ? Localization.Format($"暂保留 {Time(kept, 1)}")
            : Localization.Format($"保留 {kept / result.Info.Duration:0%} · 移除 {Time(Math.Max(0, result.Info.Duration - kept), 1)}");
        _recordSummary.Text = string.Join(" · ", new[] { result is null ? "" : Localization.Format($"{result.SampledFrames} 帧"), result?.Backend ?? "", elapsed is null ? "" : Localization.Format($"{elapsed.Value:0.##} 秒") }.Where(s => s.Length > 0));
        ToolTip.SetTip(_recordSummary, _recordSummary.Text);
    }

    private async Task LoadPreviewAsync(double seconds, int stream)
    {
        if (_disposed || _job.Inputs.FirstOrDefault() is not { } path) return;
        var token = _previewRequest.Restart(_lifetime.Token);
        try
        {
            // The engine caches encoded frames; both native decoding and file/process work stay off the UI thread.
            var bitmap = await Task.Run(async () =>
            {
                var data = await _engine.Thumbnail(path, seconds, 800, 450, token, pad: false, videoStreamIndex: stream).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using var memory = new MemoryStream(data); return new Bitmap(memory);
            }, token);
            if (_disposed || token.IsCancellationRequested || !_previewRequest.IsCurrent(token)) { bitmap.Dispose(); return; }
            var old = _preview.Source as Bitmap; _preview.Source = bitmap; old?.Dispose(); _placeholder.IsVisible = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppDiagnostics.Record("Person task preview", error);
            if (_previewRequest.IsCurrent(token)) { var old = _preview.Source as Bitmap; _preview.Source = null; old?.Dispose(); _placeholder.IsVisible = true; }
        }
    }

    public Task<byte[]> ReadSegmentThumbnail(string path, ConversionOptions options, CancellationToken ct)
        => SegmentThumbnail.ReadAsync(_engine, path, options, ct, _lifetime.Token);

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; ++_loadRevision; _lifetime.Cancel(); _previewRequest.Cancel();
        var still = _preview.Source as Bitmap; _preview.Source = null; still?.Dispose();
        _surface.Dispose(); _lifetime.Dispose();
    }

    private static string Codec(string value) => value.ToLowerInvariant() switch { "h264" => "H.264", "hevc" or "h265" => "H.265", "av1" => "AV1", _ => value };
    internal static string Time(double seconds, int precision = 3)
    {
        var time = TimeSpan.FromSeconds(Math.Round(Math.Max(0, double.IsFinite(seconds) ? seconds : 0), precision, MidpointRounding.AwayFromZero));
        var prefix = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:" : $"{time.Minutes:00}:";
        return prefix + (precision == 0 ? $"{time.Seconds:00}" : (time.Seconds + time.Milliseconds / 1000d).ToString("00." + new string('0', precision), CultureInfo.InvariantCulture));
    }
    private static TextBlock Label(string text, bool secondary = false)
    {
        var label = Ui.Text(text, secondary ? "caption" : "");
        label.FontFeatures = new() { FontFeature.Parse("tnum") }; label.VerticalAlignment = VerticalAlignment.Center; return label;
    }
    private static TextBlock Heading(string text) => new SectionHeader { Text = Localization.Text(text), Margin = new(0) };
    private static Control Legend(string text, string resource)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var dot = new Border { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center }; Resource(dot, BackgroundProperty, resource);
        Resource(dot, Border.CornerRadiusProperty, "AcCheckRadius"); row.Children.Add(dot); row.Children.Add(Label(text, true)); return row;
    }
    private static void Resource(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
}
