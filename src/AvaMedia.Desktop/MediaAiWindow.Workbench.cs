using Avalonia;
using Avalonia.Automation;
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
    private readonly ComboBox _chartSource = Ui.Combo(["标签分数（JoyTag）", "语义相似度（场景/面部）"], "标签分数（JoyTag）");
    private readonly ComboBox _scoreMode = Ui.Combo(["推荐分数", "采样峰值", "采样平均", "当前画面"], "推荐分数");
    private readonly ComboBox _tagScope = Ui.Combo(["全部标签", "NSFW", "场景", "人物特征", "姿态 / 体位"], "全部标签");
    private readonly Slider _tagThreshold = new() { Minimum = .05, Maximum = .95, Value = .4, TickFrequency = .01 };
    private readonly Slider _sceneThreshold = new() { Minimum = .05, Maximum = .95, Value = .55, TickFrequency = .01 };
    private readonly NumericUpDown _sceneMargin = new() { Minimum = 0, Maximum = .5m, Value = .03m, Increment = .01m };
    private readonly CheckBox _followLive = new() { Content = "跟随识别", IsChecked = true };
    private readonly CheckBox _autoTxt = new() { Content = "分析完成自动生成 TXT" };
    private readonly CheckBox _generateCaptions = new() { Content = "生成画面描述（本地视觉模型，可含成人内容）" };
    private readonly Button _saveTxt = new() { Content = "生成同目录 TXT" };
    private readonly Button _playSample = new() { Content = "播放此时间" };
    private readonly AiTagChart _scoreBars = new() { Height = 310 };
    private readonly AiTagChart _peakCurve = new() { Timeline = true, Height = 230 };
    private readonly Grid _charts = new() { ColumnDefinitions = new("*,1.25*"), ColumnSpacing = 12 };
    private readonly Border _scorePanel = ChartPanel();
    private readonly Border _curvePanel = ChartPanel();
    private readonly Grid _traceLegend = new() { ColumnSpacing = 8, RowSpacing = 4 };
    private readonly TextBlock _barReadout = new() { Classes = { "caption" }, MinHeight = 32, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _thresholdCaption = Ui.Text("", "caption");
    private readonly TextBlock _sceneCaption = Ui.Text("", "caption");
    private readonly TextBlock _sampleSummary = Ui.Text("", "caption");
    private readonly StackPanel _sceneThresholdRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _tagThresholdRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    // The pass line follows the chart source; the other model's threshold waits in the collapsed advanced area.
    private readonly ContentControl _primaryThreshold = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _secondaryThreshold = new();
    private readonly Expander _advancedThresholds = new() { Header = "高级阈值", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
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
        4 => tag.Category is "动作姿态" or "NSFW体位" or "NSFW姿态提示" or "人物朝向",
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
            slider.Width = 184; slider.Height = 24; slider.HorizontalAlignment = HorizontalAlignment.Left; slider.VerticalAlignment = VerticalAlignment.Center;
        }
        foreach (var caption in new[] { _thresholdCaption, _sceneCaption }) caption.MinWidth = 132;
        AutomationProperties.SetName(_tagThreshold, "JoyTag 标签分数阈值"); AutomationProperties.SetName(_sceneThreshold, "语义相似度阈值");
        _tagThresholdRow.Children.Add(_thresholdCaption); _tagThresholdRow.Children.Add(_tagThreshold);
        _sceneThresholdRow.Children.Add(_sceneCaption); _sceneThresholdRow.Children.Add(_sceneThreshold);
        var passLine = new WrapPanel { Margin = new(0, 0, 0, 2) }; passLine.Children.Add(_primaryThreshold);
        var hint = Ui.Text("影响达标标签、TXT 与重命名", "caption"); hint.Margin = new(12, 0, 0, 0); hint.TextWrapping = TextWrapping.NoWrap; passLine.Children.Add(hint);
        var thresholds = new StackPanel { Spacing = 4 }; thresholds.Children.Add(passLine);
        _secondaryThreshold.Margin = new(0, 4, 0, 0); _advancedThresholds.Content = _secondaryThreshold; thresholds.Children.Add(_advancedThresholds);
        var layout = new StackPanel { Spacing = 4 }; layout.Children.Add(panel); layout.Children.Add(thresholds);
        PlaceThresholds();
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
        return layout;
    }
    private void PlaceThresholds()
    {
        var semantic = _chartSource.SelectedIndex == 1;
        Control primary = semantic ? _sceneThresholdRow : _tagThresholdRow, secondary = semantic ? _tagThresholdRow : _sceneThresholdRow;
        if (!ReferenceEquals(_primaryThreshold.Content, primary)) { _primaryThreshold.Content = null; _secondaryThreshold.Content = null; _primaryThreshold.Content = primary; _secondaryThreshold.Content = secondary; }
        _advancedThresholds.IsVisible = secondary.IsVisible;
    }
    private Control BuildCharts()
    {
        var bars = new StackPanel { Spacing = 6 }; bars.Children.Add(Ui.Text("标签排名", "heading")); bars.Children.Add(_scoreBars); bars.Children.Add(_barReadout);
        _scorePanel.Child = bars; _scorePanel.VerticalAlignment = VerticalAlignment.Stretch; _charts.Children.Add(_scorePanel);
        var curve = new StackPanel { Spacing = 6 }; curve.Children.Add(Ui.Text("采样峰值曲线", "heading")); curve.Children.Add(_peakCurve); curve.Children.Add(_traceLegend);
        _curvePanel.Child = curve; _curvePanel.VerticalAlignment = VerticalAlignment.Stretch; Grid.SetColumn(_curvePanel, 1); _charts.Children.Add(_curvePanel); return _charts;
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
        return tags.Where(tag => TagQualifies(result, tag) && ScopeMatches(tag) && (query.Length == 0 || tag.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(tag => tag.Score).DistinctBy(TagKey, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private void RenderCharts(MediaTagResult? result)
    {
        var semantic = _chartSource.SelectedIndex == 1; var threshold = semantic ? _sceneThreshold.Value : (double)(_threshold.Value ?? .4m);
        _thresholdCaption.Text = semantic ? Localization.Format($"标签阈值 {(_threshold.Value ?? .4m):0.00}") : Localization.Format($"合格线 {(_threshold.Value ?? .4m):0.00}");
        _sceneCaption.Text = semantic ? Localization.Format($"合格线 {_sceneThreshold.Value:0.00}") : Localization.Format($"语义相似度 {_sceneThreshold.Value:0.00}");
        _sceneThresholdRow.IsVisible = semantic || _sceneTags.IsChecked == true || result?.Scenes is not null; PlaceThresholds();
        var candidates = result is null ? [] : PlotCandidates(result);
        _barReadout.Text = "";
        _chartSection.IsVisible = result is not null;
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
        var cursor = result is null ? 0 : CursorFor(result);
        _sampleSummary.Text = result is null ? "" : MediaTime.Format(cursor);
        _sampleSummary.IsVisible = result is not null && VideoFormats.IsVideo(result.Path);
        Localization.SetIsUserText(_sampleSummary, true);
        _playSample.IsEnabled = result is not null && VideoFormats.IsVideo(result.Path) && result.Frames.Count > 0;
        _scorePanel.IsVisible = bars.Length > 0;
        _curvePanel.IsVisible = series.Any(trace => trace.Points.Any(point => point.Score.HasValue));
        _charts.IsVisible = _scorePanel.IsVisible || _curvePanel.IsVisible;
        _charts.ColumnDefinitions[0].Width = _scorePanel.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _charts.ColumnDefinitions[1].Width = _curvePanel.IsVisible ? new GridLength(1.25, GridUnitType.Star) : new GridLength(0);
        _charts.ColumnSpacing = _scorePanel.IsVisible && _curvePanel.IsVisible ? 12 : 0;
        _scoreBars.Height = Math.Max(80, 28 + bars.Length * 36);
        _scoreBars.Update(bars, series, threshold, result?.DurationSeconds ?? 0, cursor, keys.LastOrDefault(), semantic);
        var thresholds = new List<TagChartThreshold>();
        if (selected.Any(tag => tag.Model == ModelCatalog.JoyTagId)) thresholds.Add(new(ModelCatalog.JoyTagId, (double)(_threshold.Value ?? .4m), false));
        if (selected.Any(tag => tag.Model == ModelCatalog.EmbeddingId)) thresholds.Add(new(ModelCatalog.EmbeddingId, _sceneThreshold.Value, true));
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
