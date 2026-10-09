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
    private readonly ComboBox _chartSource = Ui.Combo(["JoyTag · 标签分数", "EmbeddingGemma · 语义相似度"], "JoyTag · 标签分数");
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
    private readonly Grid _traceLegend = new() { ColumnSpacing = 8, RowSpacing = 4 };
    private readonly TextBlock _barReadout = new() { Classes = { "caption" }, MinHeight = 32, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _thresholdCaption = Ui.Text("", "caption");
    private readonly TextBlock _sceneCaption = Ui.Text("", "caption");
    private readonly TextBlock _chartSummary = Ui.Text("", "caption");
    private readonly TextBlock _sampleSummary = Ui.Text("", "caption");
    private readonly StackPanel _sceneThresholdRow = new() { Spacing = 2 };
    private bool _syncingThresholds, _writingTxt;
    private int _sampleSelectionGeneration;
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
        .Where(candidate => !WordLibraryCatalog.IsSemanticBaseline(candidate.Label)
            && (_onlyLibrary.IsChecked != true || _libraryCandidates.Any(entry => entry.Label.Equals(candidate.Label, StringComparison.OrdinalIgnoreCase))))
        .DistinctBy(candidate => candidate.Label)
        .Select(candidate => new ResultTag(candidate.Label, candidate.Category, SceneValue(result, candidate.Label), "cosine_similarity", ModelCatalog.EmbeddingId)) ?? [];
    private bool SceneQualifies(MediaTagResult result, ResultTag tag)
    {
        IEnumerable<MediaSceneFrame> frames = result.Scenes?.Frames ?? [];
        if (_scoreMode.SelectedIndex == 3) frames = frames.OrderBy(frame => Math.Abs(frame.Seconds - CursorFor(result))).Take(1);
        return tag.Score >= _sceneThreshold.Value && frames.Any(frame => frame.Candidates.Any(candidate => candidate.Label == tag.Label
            && candidate.Qualifies(_sceneThreshold.Value, (double)(_sceneMargin.Value ?? .03m))));
    }

    private bool TryDisplayedResult(string path, out MediaTagResult result) => _liveResults.TryGetValue(path, out result!) || _results.TryGetValue(path, out result!);
    private static string TagKey(ResultTag tag) => tag.Model + "/" + tag.Label.Trim();
    private bool ScopeMatches(ResultTag tag) => _tagScope.SelectedIndex switch
    {
        1 => tag.Category.StartsWith("NSFW", StringComparison.Ordinal),
        2 => tag.Category.StartsWith("场景", StringComparison.Ordinal) || tag.Category is "照明状态" or "画面照明",
        3 => !tag.Category.StartsWith("NSFW", StringComparison.Ordinal) && (tag.Category.Contains("特征", StringComparison.Ordinal) || tag.Category == "面部可见性"),
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
        var panel = new WrapPanel();
        var options = new WrapPanel { Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _chartSource.Width = 236; _scoreMode.Width = 138; _tagScope.Width = 128;
        foreach (var selector in new[] { _chartSource, _scoreMode, _tagScope })
        {
            selector.Margin = new(0, 0, 8, 6); options.Children.Add(selector);
        }
        panel.Children.Add(options);
        foreach (var slider in new[] { _tagThreshold, _sceneThreshold })
        {
            slider.Width = 184; slider.Height = 24; slider.HorizontalAlignment = HorizontalAlignment.Left;
        }
        var thresholds = new WrapPanel();
        var tagRow = new StackPanel { Spacing = 2, Margin = new(0, 0, 12, 6) };
        tagRow.Children.Add(_thresholdCaption); tagRow.Children.Add(_tagThreshold); thresholds.Children.Add(tagRow);
        _sceneThresholdRow.Margin = new(0, 0, 0, 6);
        _sceneThresholdRow.Children.Add(_sceneCaption); _sceneThresholdRow.Children.Add(_sceneThreshold); thresholds.Children.Add(_sceneThresholdRow); panel.Children.Add(thresholds);
        _tagThreshold.Value = (double)(_threshold.Value ?? .4m);
        _threshold.PropertyChanged += (_, change) => { if (change.Property == NumericUpDown.ValueProperty) SetThreshold((double)(_threshold.Value ?? .4m), false); };
        _tagThreshold.PropertyChanged += (_, change) => { if (change.Property == Slider.ValueProperty) SetThreshold(_tagThreshold.Value, false); };
        _sceneThreshold.PropertyChanged += (_, change) => { if (change.Property == Slider.ValueProperty && !_syncingThresholds) RefreshDisplayedResults(); };
        _sceneMargin.PropertyChanged += (_, change) => { if (change.Property == NumericUpDown.ValueProperty) RefreshDisplayedResults(); };
        _chartSource.SelectionChanged += (_, _) => RenderSelectedResult();
        _scoreMode.SelectionChanged += (_, _) => RefreshDisplayedResults();
        _tagScope.SelectionChanged += (_, _) => RenderSelectedResult();
        _scoreBars.ThresholdEdited += value => SetThreshold(value, _chartSource.SelectedIndex == 1);
        _peakCurve.ModelThresholdEdited += (model, value) => SetThreshold(value, model == ModelCatalog.EmbeddingId);
        _scoreBars.TagSelected += key => SelectTrace(key);
        _peakCurve.SampleSelected += seconds => { _ = SelectSampleAsync(seconds); };
        _peakCurve.SampleHovered += RefreshLegendSample;
        _scoreBars.BarHovered += bar =>
        {
            _barReadout.Text = bar is null ? "" : bar.Label + " · " + Localization.Text("峰值") + $" {bar.Peak:0.000} · " + Localization.Text("平均") + $" {bar.Average:0.000}";
            Localization.SetIsUserText(_barReadout, true);
        };
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
        var bars = new StackPanel { Spacing = 6 }; bars.Children.Add(Ui.Text("标签排名", "heading")); bars.Children.Add(_chartSummary); bars.Children.Add(_scoreBars); bars.Children.Add(_barReadout);
        var left = ChartPanel(bars); left.VerticalAlignment = VerticalAlignment.Stretch; charts.Children.Add(left);
        var curve = new StackPanel { Spacing = 6 }; curve.Children.Add(Ui.Text("采样峰值曲线", "heading")); curve.Children.Add(_peakCurve); curve.Children.Add(_traceLegend);
        var right = ChartPanel(curve); right.VerticalAlignment = VerticalAlignment.Stretch; Grid.SetColumn(right, 1); charts.Children.Add(right); return charts;
    }
    private static Border ChartPanel(Control? content = null)
    {
        var panel = new Border { Child = content, Padding = new(12), CornerRadius = new(6), BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Top };
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
    private ResultTag[] PlotCandidates(MediaTagResult result, bool? semantic = null)
    {
        var tags = (semantic ?? _chartSource.SelectedIndex == 1) ? SceneCandidates(result) : JoyCandidates(result);
        var query = _tagSearch.Text?.Trim() ?? "";
        return tags.Where(tag => double.IsFinite(tag.Score) && ScopeMatches(tag) && (query.Length == 0 || tag.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(tag => tag.Score).DistinctBy(TagKey, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private void RenderCharts(MediaTagResult? result)
    {
        var semantic = _chartSource.SelectedIndex == 1; var threshold = semantic ? _sceneThreshold.Value : (double)(_threshold.Value ?? .4m);
        _thresholdCaption.Text = Localization.Format($"标签阈值 {(_threshold.Value ?? .4m):0.00}");
        _sceneCaption.Text = Localization.Format($"语义相似度 {_sceneThreshold.Value:0.00}"); _sceneThresholdRow.IsVisible = _sceneTags.IsChecked == true || result?.Scenes is not null;
        var candidates = result is null ? [] : PlotCandidates(result);
        _barReadout.Text = "";
        var keys = result is null ? new List<string>() : _traces.GetValueOrDefault(result.Path) ?? [];
        var selected = result is null ? [] : new[] { false, true }.SelectMany(scene =>
        {
            var model = scene ? ModelCatalog.EmbeddingId : ModelCatalog.JoyTagId;
            var modelKeys = keys.Where(key => key.StartsWith(model + "/", StringComparison.Ordinal)).ToArray();
            return PlotCandidates(result, scene).Where(tag => modelKeys.Length == 0 || modelKeys.Contains(TagKey(tag))).Take(3);
        }).ToArray();
        var series = result is null ? [] : selected.GroupBy(tag => tag.Model).SelectMany(group => group.Select((tag, variant) =>
            new TagChartSeries(TagKey(tag), TagPoints(result, tag), tag.Model,
                tag.Model == ModelCatalog.EmbeddingId, variant))).ToArray();
        RenderTraceLegend(selected, series);
        var bars = candidates.Take(8).Select(tag =>
        {
            var points = TagPoints(result!, tag).Where(point => point.Score.HasValue).Select(point => point.Score!.Value).ToArray();
            return new TagChartBar(TagKey(tag), tag.Label, tag.Score, points.Length > 0 ? points.Max() : tag.Score,
                points.Length > 0 ? points.Average() : tag.Score, CurrentScore(result!, tag));
        }).ToArray();
        _chartSummary.Text = result is null ? Localization.Text("等待识别数据") : Localization.Format($"采样 {(semantic ? result.Scenes?.Frames.Count ?? 0 : result.Frames.Count)}/{result.SampledFrames} 帧 · 达标 {candidates.Count(tag => semantic ? SceneQualifies(result, tag) : tag.Score >= threshold)} 个");
        var cursor = result is null ? 0 : CursorFor(result);
        _sampleSummary.Text = result is null ? "" : MediaTime.Format(cursor);
        _sampleSummary.IsVisible = result is not null && VideoFormats.IsVideo(result.Path);
        Localization.SetIsUserText(_sampleSummary, true);
        _playSample.IsEnabled = result is not null && VideoFormats.IsVideo(result.Path) && result.Frames.Count > 0;
        _scoreBars.Height = bars.Length == 0 ? 180 : 28 + bars.Length * 36;
        _scoreBars.Update(bars, series, threshold, result?.DurationSeconds ?? 0, cursor, keys.LastOrDefault(), semantic);
        var thresholds = new List<TagChartThreshold>();
        if (result?.Scores.Count > 0) thresholds.Add(new(ModelCatalog.JoyTagId, (double)(_threshold.Value ?? .4m), false));
        if (result?.Scenes is not null) thresholds.Add(new(ModelCatalog.EmbeddingId, _sceneThreshold.Value, true));
        _peakCurve.Update(bars, series, threshold, result?.DurationSeconds ?? 0, cursor, keys.LastOrDefault(), semantic, thresholds);
    }
    private static string ModelLabel(string model) => model == ModelCatalog.EmbeddingId ? "EmbeddingGemma" : "JoyTag";
    private static string ModelColor(string model) => model == ModelCatalog.EmbeddingId ? "UiSuccess" : "UiAccent";
    private void SelectTrace(string key)
    {
        if (_list.SelectedItem is not MediaFileEntry entry) return;
        var semantic = key.StartsWith(ModelCatalog.EmbeddingId + "/", StringComparison.Ordinal);
        _chartSource.SelectedIndex = semantic ? 1 : 0;
        var model = semantic ? ModelCatalog.EmbeddingId : ModelCatalog.JoyTagId;
        var stored = _traces.GetValueOrDefault(entry.Path)?.ToList() ?? [];
        var keys = stored.Where(value => value.StartsWith(model + "/", StringComparison.Ordinal)).ToList();
        stored.RemoveAll(value => value.StartsWith(model + "/", StringComparison.Ordinal));
        if (TryDisplayedResult(entry.Path, out var result))
        {
            var ranked = PlotCandidates(result, semantic);
            keys = ranked.Where(tag => keys.Count == 0 || keys.Contains(TagKey(tag))).Take(3).Select(TagKey).ToList();
        }
        if (!keys.Remove(key)) { if (keys.Count >= 3) keys.RemoveAt(keys.Count - 1); keys.Add(key); }
        stored.AddRange(keys);
        if (stored.Count == 0) _traces.Remove(entry.Path); else _traces[entry.Path] = stored;
        RenderSelectedResult();
    }
    private async Task SelectSampleAsync(double seconds)
    {
        if (_list.SelectedItem is not MediaFileEntry entry || !TryDisplayedResult(entry.Path, out var result)
            || !VideoFormats.IsVideo(result.Path) || !double.IsFinite(seconds)) return;
        var generation = ++_sampleSelectionGeneration;
        seconds = Math.Clamp(seconds, 0, Math.Max(0, result.DurationSeconds - .001));
        _positions[result.Path] = seconds; _followLive.IsChecked = false;
        _peakCurve.UpdateCursor(seconds); _sampleSummary.Text = MediaTime.Format(seconds);
        var sample = _legendValues.Values.SelectMany(value => value.Points).Where(point => point.Score.HasValue)
            .MinBy(point => Math.Abs(point.Seconds - seconds));
        RefreshLegendSample(sample?.Seconds);
        await RefreshSelectedPreviewAsync(seconds, debounce: true);
        if (_closed || generation != _sampleSelectionGeneration || _list.SelectedItem != entry || _peakCurve.IsSeeking || _scoreMode.SelectedIndex != 3) return;
        if (_results.TryGetValue(entry.Path, out var completed)) ShowResult(entry, completed);
        RenderSelectedResult();
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
