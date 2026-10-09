using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly Dictionary<string, MediaTagResult> _liveResults = new(BatchRename.PathComparer);
    private readonly Dictionary<string, List<string>> _traces = new(BatchRename.PathComparer);
    private readonly Dictionary<string, double> _positions = new(BatchRename.PathComparer);
    private readonly Dictionary<string, string> _reportSources = new(BatchRename.PathComparer);
    private readonly ComboBox _chartSource = Ui.Combo(["标签分数", "场景相似度"], "标签分数");
    private readonly ComboBox _scoreMode = Ui.Combo(["推荐分数", "采样峰值", "采样平均", "当前画面"], "推荐分数");
    private readonly ComboBox _tagScope = Ui.Combo(["全部标签", "NSFW", "场景", "人物特征"], "全部标签");
    private readonly Slider _tagThreshold = new() { Minimum = .05, Maximum = .95, Value = .4, TickFrequency = .01 };
    private readonly Slider _sceneThreshold = new() { Minimum = .05, Maximum = .95, Value = .55, TickFrequency = .01 };
    private readonly NumericUpDown _sceneMargin = new() { Minimum = 0, Maximum = .5m, Value = .03m, Increment = .01m };
    private readonly CheckBox _followLive = new() { Content = "跟随识别", IsChecked = true };
    private readonly CheckBox _autoTxt = new() { Content = "分析完成自动生成 TXT" };
    private readonly Button _saveTxt = new() { Content = "生成同目录 TXT", Classes = { "primary" } };
    private readonly Button _playSample = new() { Content = "播放此时间" };
    private readonly AiTagChart _scoreBars = new() { Height = 310 };
    private readonly AiTagChart _peakCurve = new() { Timeline = true, Height = 230 };
    private readonly WrapPanel _traceLegend = new();
    private readonly TextBlock _thresholdCaption = Ui.Text("", "caption");
    private readonly TextBlock _sceneCaption = Ui.Text("", "caption");
    private readonly TextBlock _chartSummary = Ui.Text("", "caption");
    private readonly TextBlock _sampleSummary = Ui.Text("", "caption");
    private readonly StackPanel _sceneThresholdRow = new() { Spacing = 3 };
    private bool _syncingThresholds, _writingTxt;
    private MediaTagResult? _indexedResult;
    private Dictionary<string, int> _scoreIndices = new(StringComparer.OrdinalIgnoreCase);
    private double CurrentJoyValue(MediaTagResult result, string tag)
    {
        if (!ReferenceEquals(_indexedResult, result))
        {
            _scoreIndices = result.Scores.Select((score, index) => (score.Tag, index)).ToDictionary(item => item.Tag, item => item.index, StringComparer.OrdinalIgnoreCase);
            _indexedResult = result;
        }
        var frame = result.Frames.MinBy(frame => Math.Abs(frame.Seconds - CursorFor(result)));
        return frame is not null && _scoreIndices.TryGetValue(tag, out var index) && index < frame.Values.Length ? frame.Values[index] : double.NaN;
    }
    private double SceneValue(MediaTagResult result, string label)
    {
        var points = MediaTagTimeline.Points(result, [], label).Where(point => point.Score.HasValue).ToArray();
        if (points.Length == 0) return double.NaN;
        return _scoreMode.SelectedIndex switch
        {
            2 => points.Average(point => point.Score!.Value),
            3 => points.MinBy(point => Math.Abs(point.Seconds - CursorFor(result)))!.Score!.Value,
            _ => points.Max(point => point.Score!.Value)
        };
    }
    private IEnumerable<ResultTag> SceneCandidates(MediaTagResult result) => result.Scenes?.Frames.SelectMany(frame => frame.Candidates)
        .Where(candidate => candidate.Label is not ("其他室内" or "照明不明")).DistinctBy(candidate => candidate.Label)
        .Select(candidate => new ResultTag(candidate.Label, candidate.Category, SceneValue(result, candidate.Label), "cosine_similarity", ModelCatalog.EmbeddingId)) ?? [];
    private bool SceneQualifies(MediaTagResult result, ResultTag tag)
    {
        IEnumerable<MediaSceneFrame> frames = result.Scenes?.Frames ?? [];
        if (_scoreMode.SelectedIndex == 3) frames = frames.OrderBy(frame => Math.Abs(frame.Seconds - CursorFor(result))).Take(1);
        return tag.Score >= _sceneThreshold.Value && frames.Any(frame => frame.Candidates.Any(candidate => candidate.Label == tag.Label
            && candidate.Similarity >= _sceneThreshold.Value && candidate.Margin >= (double)(_sceneMargin.Value ?? .03m)));
    }

    private bool TryDisplayedResult(string path, out MediaTagResult result) => _liveResults.TryGetValue(path, out result!) || _results.TryGetValue(path, out result!);
    private static string TagKey(ResultTag tag) => tag.Model + "/" + tag.Label;
    private bool ScopeMatches(ResultTag tag) => _tagScope.SelectedIndex switch
    {
        1 => tag.Category.StartsWith("NSFW", StringComparison.Ordinal),
        2 => tag.Category.StartsWith("场景", StringComparison.Ordinal) || tag.Category is "照明状态" or "画面照明",
        3 => !tag.Category.StartsWith("NSFW", StringComparison.Ordinal) && tag.Category.Contains("特征", StringComparison.Ordinal),
        _ => true
    };
    private double CursorFor(MediaTagResult result) => _positions.GetValueOrDefault(result.Path);
    private MediaTagPoint[] TagPoints(MediaTagResult result, ResultTag tag) => MediaTagTimeline.Points(result, tag.RawTags ?? [], tag.Model == ModelCatalog.EmbeddingId ? tag.Label : null);
    private double? CurrentScore(MediaTagResult result, ResultTag tag)
    {
        var points = TagPoints(result, tag).Where(point => point.Score.HasValue).ToArray();
        return points.OrderBy(point => Math.Abs(point.Seconds - CursorFor(result))).FirstOrDefault()?.Score;
    }
    private double JoyValue(MediaTagResult result, MediaTagScore score)
    {
        if (!VideoFormats.IsVideo(result.Path)) return score.Score;
        return _scoreMode.SelectedIndex switch
        {
            1 => score.Maximum, 2 => score.Score,
            3 => CurrentJoyValue(result, score.Tag),
            _ => MediaTagService.TagSignal(result, score)
        };
    }
    private string JoyScoreKind(MediaTagResult result, string[] tags) => !VideoFormats.IsVideo(result.Path) ? "score" : _scoreMode.SelectedIndex switch
    {
        1 => "sample_peak", 2 => "sample_average", 3 => "current_frame",
        _ => tags.All(WordLibraryCatalog.UsesSamplePeak) ? "sample_peak" : "sample_average"
    };
    private Control BuildWorkbench()
    {
        var panel = new StackPanel { Spacing = 10 };
        var options = new Grid { ColumnDefinitions = new("*,*,*,Auto"), ColumnSpacing = 8 };
        Control[] selectors = [_chartSource, _scoreMode, _tagScope, _followLive];
        for (var i = 0; i < selectors.Length; i++) { Grid.SetColumn(selectors[i], i); options.Children.Add(selectors[i]); }
        panel.Children.Add(options);
        var thresholds = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
        var tagRow = new StackPanel { Spacing = 3 }; tagRow.Children.Add(_thresholdCaption); tagRow.Children.Add(_tagThreshold); thresholds.Children.Add(tagRow);
        _sceneThresholdRow.Children.Add(_sceneCaption); _sceneThresholdRow.Children.Add(_sceneThreshold); Grid.SetColumn(_sceneThresholdRow, 1); thresholds.Children.Add(_sceneThresholdRow); panel.Children.Add(thresholds);
        _tagThreshold.Value = (double)(_threshold.Value ?? .4m);
        _threshold.PropertyChanged += (_, change) => { if (change.Property == NumericUpDown.ValueProperty) SetThreshold((double)(_threshold.Value ?? .4m), false); };
        _tagThreshold.PropertyChanged += (_, change) => { if (change.Property == Slider.ValueProperty) SetThreshold(_tagThreshold.Value, false); };
        _sceneThreshold.PropertyChanged += (_, change) => { if (change.Property == Slider.ValueProperty && !_syncingThresholds) RefreshDisplayedResults(); };
        _sceneMargin.PropertyChanged += (_, change) => { if (change.Property == NumericUpDown.ValueProperty) RefreshDisplayedResults(); };
        _chartSource.SelectionChanged += (_, _) => RenderSelectedResult();
        _scoreMode.SelectionChanged += (_, _) => RefreshDisplayedResults();
        _tagScope.SelectionChanged += (_, _) => RenderSelectedResult();
        _scoreBars.ThresholdEdited += value => SetThreshold(value, _chartSource.SelectedIndex == 1);
        _peakCurve.ThresholdEdited += value => SetThreshold(value, _chartSource.SelectedIndex == 1);
        _scoreBars.TagSelected += key => SelectTrace(key);
        _peakCurve.SampleSelected += seconds => { _ = SelectSampleAsync(seconds); };
        _playSample.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not MediaFileEntry entry || !TryDisplayedResult(entry.Path, out var result)) return;
            try { MediaTagService.ValidateSource(result); var player = new PlayerWindow(_engine); player.ShowForPlayback(this); await player.OpenAtAsync(result.Path, CursorFor(result)); }
            catch (Exception error) { await Ui.Message(this, "无法播放", error.Message); }
        };
        _saveTxt.Click += async (_, _) => await SaveTextReportsAsync();
        return panel;
    }
    private Control BuildCharts()
    {
        var charts = new Grid { ColumnDefinitions = new("*,1.25*"), ColumnSpacing = 12 };
        var bars = new StackPanel { Spacing = 8 }; bars.Children.Add(Ui.Text("标签柱状图", "heading")); bars.Children.Add(_chartSummary); bars.Children.Add(_scoreBars);
        charts.Children.Add(ChartPanel(bars));
        var curve = new StackPanel { Spacing = 8 }; curve.Children.Add(Ui.Text("采样峰值曲线", "heading")); curve.Children.Add(_traceLegend); curve.Children.Add(_peakCurve);
        var sample = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; sample.Children.Add(_sampleSummary); Grid.SetColumn(_playSample, 1); sample.Children.Add(_playSample); curve.Children.Add(sample);
        var right = ChartPanel(curve); Grid.SetColumn(right, 1); charts.Children.Add(right); return charts;
    }
    private static Border ChartPanel(Control content)
    {
        var panel = new Border { Child = content, Padding = new(12), CornerRadius = new(6), BorderThickness = new(1) };
        panel.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("UiControlRadius"));
        panel.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised")); panel.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiBorder")); return panel;
    }
    private void SetThreshold(double value, bool scene)
    {
        if (_syncingThresholds) return;
        _syncingThresholds = true;
        if (scene) _sceneThreshold.Value = Math.Round(value, 2);
        else { _threshold.Value = (decimal)Math.Round(value, 2); _threshold.Text = _threshold.Value?.ToString(_threshold.NumberFormat); _tagThreshold.Value = (double)_threshold.Value!; }
        _syncingThresholds = false; RefreshDisplayedResults();
    }
    private ResultTag[] PlotCandidates(MediaTagResult result)
    {
        IEnumerable<ResultTag> tags;
        if (_chartSource.SelectedIndex == 1) tags = SceneCandidates(result);
        else if (_onlyLibrary.IsChecked == true)
        {
            var scores = result.Scores.ToDictionary(score => score.Tag, score => JoyValue(result, score), StringComparer.OrdinalIgnoreCase);
            tags = _libraryCandidates.Where(candidate => candidate.Tags.Length > 0 && candidate.Tags.All(scores.ContainsKey))
                .Select(candidate => new ResultTag(candidate.Label, candidate.Category, candidate.Tags.Min(tag => scores[tag]), JoyScoreKind(result, candidate.Tags), RawTags: candidate.Tags));
        }
        else tags = result.Scores.Select(score => new ResultTag(WordLibraryCatalog.TagLabel(score.Tag), WordLibraryCatalog.TagCategory(score.Tag), JoyValue(result, score), JoyScoreKind(result, [score.Tag]), RawTags: [score.Tag]));
        var query = _tagSearch.Text?.Trim() ?? "";
        return tags.Where(tag => double.IsFinite(tag.Score) && ScopeMatches(tag) && (query.Length == 0 || tag.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(tag => tag.Score).DistinctBy(TagKey).ToArray();
    }
    private void RenderCharts(MediaTagResult? result)
    {
        var semantic = _chartSource.SelectedIndex == 1; var threshold = semantic ? _sceneThreshold.Value : (double)(_threshold.Value ?? .4m);
        _thresholdCaption.Text = Localization.Format($"标签阈值 {(_threshold.Value ?? .4m):0.00}");
        _sceneCaption.Text = Localization.Format($"场景相似度 {_sceneThreshold.Value:0.00}"); _sceneThresholdRow.IsVisible = _sceneTags.IsChecked == true || result?.Scenes is not null;
        var candidates = result is null ? [] : PlotCandidates(result);
        _traceLegend.Children.Clear();
        var keys = result is null ? new List<string>() : _traces.GetValueOrDefault(result.Path) ?? [];
        keys = keys.Where(key => key.StartsWith(ModelCatalog.EmbeddingId + "/", StringComparison.Ordinal) == semantic).ToList();
        // Automatic candidates remain derived from the latest scores, rather than becoming a manual selection.
        var selected = candidates.Where(tag => keys.Count == 0 || keys.Contains(TagKey(tag))).Take(3).ToArray();
        var series = result is null ? [] : selected.Select(tag => new TagChartSeries(TagKey(tag), tag.Label, TagPoints(result, tag))).ToArray();
        foreach (var tag in selected)
        {
            var legend = Ui.Button(tag.Label + $" · {tag.Score:0.000}", () => SelectTrace(TagKey(tag))); legend.Margin = new(0, 0, 6, 4);
            legend.Bind(Button.BorderBrushProperty, new DynamicResourceExtension(new[] { "UiAccent", "UiSuccess", "UiWarning" }[Array.IndexOf(selected, tag)])); legend.BorderThickness = new(2);
            Localization.SetIsUserText(legend, true); _traceLegend.Children.Add(legend);
        }
        var bars = candidates.Take(12).Select(tag =>
        {
            var points = TagPoints(result!, tag).Where(point => point.Score.HasValue).Select(point => point.Score!.Value).ToArray();
            return new TagChartBar(TagKey(tag), tag.Label, tag.Score, points.Length > 0 ? points.Max() : tag.Score,
                points.Length > 0 ? points.Average() : tag.Score, CurrentScore(result!, tag));
        }).ToArray();
        _chartSummary.Text = result is null ? Localization.Text("等待识别数据") : Localization.Format($"采样 {(semantic ? result.Scenes?.Frames.Count ?? 0 : result.Frames.Count)}/{result.SampledFrames} 帧 · 达标 {candidates.Count(tag => semantic ? SceneQualifies(result, tag) : tag.Score >= threshold)} 个");
        var cursor = result is null ? 0 : CursorFor(result);
        _sampleSummary.Text = result is null ? "" : MediaTime.Format(cursor) + " · " + Localization.Text("点击曲线定位采样画面");
        Localization.SetIsUserText(_sampleSummary, true);
        _playSample.IsEnabled = result is not null && VideoFormats.IsVideo(result.Path) && result.Frames.Count > 0;
        _scoreBars.Update(bars, series, threshold, result?.DurationSeconds ?? 0, cursor, keys.LastOrDefault(), semantic);
        _peakCurve.Update(bars, series, threshold, result?.DurationSeconds ?? 0, cursor, keys.LastOrDefault(), semantic);
    }
    private void SelectTrace(string key)
    {
        if (_list.SelectedItem is not MediaFileEntry entry) return;
        var semantic = key.StartsWith(ModelCatalog.EmbeddingId + "/", StringComparison.Ordinal);
        _chartSource.SelectedIndex = semantic ? 1 : 0;
        var keys = _traces.GetValueOrDefault(entry.Path)?.ToList() ?? [];
        keys.RemoveAll(value => value.StartsWith(ModelCatalog.EmbeddingId + "/", StringComparison.Ordinal) != semantic);
        if (TryDisplayedResult(entry.Path, out var result))
        {
            var ranked = PlotCandidates(result);
            keys = ranked.Where(tag => keys.Count == 0 || keys.Contains(TagKey(tag))).Take(3).Select(TagKey).ToList();
        }
        if (!keys.Remove(key)) { if (keys.Count >= 3) keys.RemoveAt(keys.Count - 1); keys.Add(key); }
        if (keys.Count == 0) _traces.Remove(entry.Path); else _traces[entry.Path] = keys;
        RenderSelectedResult();
    }
    private async Task SelectSampleAsync(double seconds)
    {
        if (_list.SelectedItem is not MediaFileEntry entry || !TryDisplayedResult(entry.Path, out var result)) return;
        _positions[result.Path] = seconds; _followLive.IsChecked = false; RenderSelectedResult();
        if (_scoreMode.SelectedIndex == 3) RefreshDisplayedResults();
        await RefreshSelectedPreviewAsync(seconds);
    }
    private async Task SaveTextReportsAsync(string[]? requested = null)
    {
        if (_writingTxt || _closed) return;
        var entries = _entries.Where(entry => requested is null ? entry.Include : requested.Contains(entry.Path, BatchRename.PathComparer))
            .Where(entry => _results.ContainsKey(entry.Path)).ToArray();
        if (entries.Length == 0) return;
        var token = _lifetime.Token; var threshold = (double)(_threshold.Value ?? .4m);
        var sceneThreshold = _sceneThreshold.Value; var sceneMargin = (double)(_sceneMargin.Value ?? .03m);
        var reports = entries.Select(entry => (Entry: entry, Result: _results[entry.Path], Labels: ResultTags(_results[entry.Path])
            .Select(tag => new MediaTagTextLabel(tag.Label, tag.Category, tag.Score, tag.ScoreKind, tag.Model, tag.RawTags ?? [])).ToArray())).ToArray();
        _writingTxt = true; UpdateActions(); var saved = 0; var failures = new List<string>();
        try
        {
            foreach (var report in reports)
            {
                token.ThrowIfCancellationRequested(); var entry = report.Entry;
                try
                {
                    var output = await MediaTagText.SaveAsync(report.Result, report.Labels, threshold, sceneThreshold,
                        sceneMargin, token, _reportSources.GetValueOrDefault(entry.Path));
                    _reportSources[entry.Path] = entry.Path; saved++; entry.Details = Path.GetFileName(output);
                }
                catch (Exception error) when (error is not OperationCanceledException) { failures.Add(entry.Name + " · " + error.Message); }
            }
            if (_closed) return;
            _status.Text = Localization.Format($"已生成 {saved} 个同目录 TXT");
            if (failures.Count > 0) await Ui.Message(this, "TXT 生成失败", string.Join(Environment.NewLine, failures));
        }
        catch (OperationCanceledException) { }
        finally { _writingTxt = false; if (!_closed) UpdateActions(); }
    }
}
