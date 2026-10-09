using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly TextBlock _detailTitle = Ui.Text("识别结果", "heading");
    // Only live/partial progress; the file list already shows each file's status and tag count.
    private readonly TextBlock _detailState = new() { Classes = { "caption" }, IsVisible = false };
    private readonly TextBox _tagSearch = new() { Watermark = "查找标签", Name = "MediaAiTagSearch", Width = 240 };
    private readonly TextBlock _tagCount = Ui.Text("标签", "heading");
    private readonly Border _resultFilters = new() { IsVisible = false };
    private readonly Button _editTags = new() { Content = "编辑标签…", IsVisible = false };
    private readonly Button _restoreTags = new() { Content = "恢复识别标签", IsVisible = false };
    private readonly Border _tagPanel = ChartPanel();
    private readonly StackPanel _tagGroups = new() { Spacing = 10 };
    private readonly Image _preview = new() { Name = "MediaAiPreview", Width = 240, Height = 240, Stretch = Stretch.Uniform, IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _resultSummary = ChartPanel();
    private readonly StackPanel _resultContent = new() { Spacing = 10 };
    private bool? _videoContext;
    private readonly Expander _details = new() { Header = "识别详情", IsVisible = false };
    private Bitmap? _previewBitmap;
    private CancellationTokenSource? _previewRequest;
    private string? _previewPath;
    private double? _previewSeconds;
    private readonly SemaphoreSlim _previewGate = new(1, 1);

    private readonly TextBlock _nsfwBadgeText = new() { FontWeight = FontWeight.SemiBold };
    private readonly Button _nsfwBadge = new() { Name = "MediaAiNsfwBadge", IsVisible = false, Padding = new(8, 2), MinHeight = 0, BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _nsfwEvidence = new() { Spacing = 5, Width = 340 };
    private readonly Expander _chartSection = new() { Header = "分数与曲线", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private Control BuildResultPane()
    {
        // Fixed summary: file, state, preview and the actions that act on the result.
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        heading.Children.Add(_preview);
        _detailTitle.MaxLines = 2; _detailTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        var titleRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        titleRow.Children.Add(_detailTitle); Grid.SetColumn(_nsfwBadge, 1); titleRow.Children.Add(_nsfwBadge);
        _nsfwBadge.Content = _nsfwBadgeText; ToolTip.SetTip(_nsfwBadge, "点击查看 NSFW 判断依据");
        _nsfwBadge.Bind(Button.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
        _nsfwBadge.Click += (_, _) => { _details.IsExpanded = !_details.IsExpanded; _details.BringIntoView(); };
        var title = new StackPanel { Spacing = 6 };
        title.Children.Add(titleRow); title.Children.Add(_detailState);
        _sampleSummary.Classes.Add("time");
        _resultFilters.Child = BuildWorkbench(); title.Children.Add(_resultFilters);
        Grid.SetColumn(title, 1); heading.Children.Add(title);
        // Charts stay first; controls use the available space beside the media preview.
        var content = _resultContent;
        var analysis = new StackPanel { Spacing = 10, Margin = new(0, 8, 0, 0) };
        analysis.Children.Add(BuildCharts()); _chartSection.Content = analysis; content.Children.Add(_chartSection);
        var tags = new StackPanel { Spacing = 10 };
        var tagHeading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        tagHeading.Children.Add(_tagCount); Grid.SetColumn(_copy, 1); tagHeading.Children.Add(_copy);
        tags.Children.Add(tagHeading); tags.Children.Add(_tagGroups); _tagPanel.Child = tags; content.Children.Add(_tagPanel);
        content.Children.Add(_details);
        _editTags.Click += async (_, _) =>
        {
            if (_list.SelectedItem is MediaFileEntry entry && _results.TryGetValue(entry.Path, out var result)) await EditTagsAsync(result);
        };
        _restoreTags.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not MediaFileEntry entry || !_editedTags.ContainsKey(entry.Path)) return;
            if (!await Ui.Confirm(this, "恢复识别标签", "恢复为模型识别的标签？此文件手动编辑的标签将被丢弃。", "恢复识别标签") || _closed) return;
            _editedTags.Remove(entry.Path); RefreshDisplayedResults();
        };
        var pane = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10 };
        _resultSummary.Child = heading; pane.Children.Add(_resultSummary);
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); pane.Children.Add(scroll);
        return pane;
    }
    private void UpdateNsfwBadge(NsfwAssessment? moderation, bool video = false)
    {
        _nsfwBadge.IsVisible = moderation is not null && moderation.State != NsfwSignalState.NoEvidence; _nsfwEvidence.Children.Clear();
        if (moderation is null || moderation.State == NsfwSignalState.NoEvidence) return;
        var (text, brush) = moderation.State == NsfwSignalState.Suspected ? ("疑似 NSFW", "UiDanger") : ("提示", "UiWarning");
        _nsfwBadgeText.Text = Localization.Text(text);
        _nsfwBadge.Bind(Button.ForegroundProperty, new DynamicResourceExtension(brush));
        _nsfwBadge.Bind(Button.BorderBrushProperty, new DynamicResourceExtension(brush));
        AutomationProperties.SetName(_nsfwBadge, "NSFW · " + Localization.Text(text));
        var state = Ui.Text(NsfwStateText(moderation.State)); state.FontWeight = FontWeight.SemiBold; _nsfwEvidence.Children.Add(state);
        if (moderation.Classifier is { Suspected: true } classifier)
        {
            _nsfwEvidence.Children.Add(Ui.Text(video
                ? FormattableString.Invariant($"Marqo NSFW · 峰值 {classifier.Maximum:0.000} · 平均 {classifier.Average:0.000}")
                : "Marqo NSFW · " + Localization.Text("分数") + $" {classifier.Maximum:0.000}"));
            _nsfwEvidence.Children.Add(Ui.Text(FormattableString.Invariant($"分类阈值 {classifier.Threshold:0.00} · {classifier.Backend}"), "caption"));
            foreach (var frame in classifier.Frames.Where(frame => video && frame.Score >= classifier.Threshold))
                _nsfwEvidence.Children.Add(Ui.Text(FormattableString.Invariant($"{MediaTime.Format(frame.Seconds)} · NSFW {frame.Score:0.000}"), "caption"));
            if (classifier.FallbackReason is not null) _nsfwEvidence.Children.Add(Ui.Text(classifier.FallbackReason, "caption"));
        }
        _nsfwEvidence.Children.Add(Ui.Text(Localization.Format($"判断阈值 {moderation.Threshold:0.00}"), "caption"));
        foreach (var item in moderation.Evidence)
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
            var kind = Ui.Text(item.Risk ? "风险" : "提示", item.Risk ? "error" : "caption"); row.Children.Add(kind);
            var label = Ui.Text(item.Label + " · " + Localization.Text(item.Category)); Localization.SetIsUserText(label, true);
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var score = Ui.Text($"{item.Signal:0.00}", "caption"); Localization.SetIsUserText(score, true); Grid.SetColumn(score, 2); row.Children.Add(score);
            _nsfwEvidence.Children.Add(row);
        }
    }
    private void RenderSelectedResult()
    {
        if (_closed) return;
        _tagGroups.Children.Clear(); _details.IsVisible = false;
        var entry = _list.SelectedItem as MediaFileEntry;
        var video = entry is not null && VideoFormats.IsVideo(entry.Path);
        _resultSummary.IsVisible = entry is not null;
        if (_videoContext != video)
        {
            _videoContext = video; _chartSection.IsExpanded = video;
            _chartSection.Header = Localization.Text(video ? "分数与曲线" : "标签分数");
            var first = video ? (Control)_chartSection : _tagPanel;
            _resultContent.Children.Remove(first); _resultContent.Children.Insert(0, first);
        }
        AutomationProperties.SetName(_preview, Localization.Text(video ? "视频预览" : "图片预览"));
        _detailTitle.Text = entry?.Name ?? Localization.Text("识别结果"); Localization.SetIsUserText(_detailTitle, true);
        ToolTip.SetTip(_detailTitle, entry?.Path);
        _tagSearch.IsVisible = entry is not null && TryDisplayedResult(entry.Path, out _);
        _tagPanel.IsVisible = _tagSearch.IsVisible || entry?.Status == Localization.Text("失败") && entry.Details.Length > 0;
        _editTags.IsVisible = entry is not null && _results.ContainsKey(entry.Path);
        _restoreTags.IsVisible = _editTags.IsVisible && _editedTags.ContainsKey(entry!.Path);
        if (entry is null || !TryDisplayedResult(entry.Path, out var result))
        {
            _detailState.Text = ""; _detailState.IsVisible = false;
            if (entry?.Status == Localization.Text("失败") && entry.Details.Length > 0) _tagGroups.Children.Add(Ui.Text(entry.Details, "error"));
            RenderCharts(null); UpdateNsfwBadge(null); UpdateActions(); return;
        }
        RenderCharts(result);
        if (!_busy && _liveResults.ContainsKey(result.Path) && entry.Details.Length > 0 && entry.Status == Localization.Text("失败")) _tagGroups.Children.Add(Ui.Text(entry.Details, "error"));
        if (result.SceneError is not null) _tagGroups.Children.Add(result.SceneSkipped ? Ui.Text(Localization.Text(result.SceneError), "caption")
            : Ui.Text(Localization.Text("语义识别失败：") + result.SceneError, "error"));
        if (result.CaptionError is not null) _tagGroups.Children.Add(Ui.Text(Localization.Text("画面描述失败：") + result.CaptionError, "error"));
        else if (!string.IsNullOrWhiteSpace(result.Caption))
        {
            var caption = new StackPanel { Spacing = 4 };
            caption.Children.Add(Ui.Text("画面描述", "heading"));
            var body = Ui.Text(result.Caption, "caption"); body.TextWrapping = TextWrapping.Wrap; Localization.SetIsUserText(body, true);
            caption.Children.Add(body);
            if (result.CaptionModel is not null) caption.Children.Add(Ui.Text(Localization.Text("描述模型") + " · " + result.CaptionModel, "caption"));
            _tagGroups.Children.Add(caption);
        }
        var tags = ResultTags(result, search: true).ToArray();
        _tagCount.Text = tags.Length > 0 ? Localization.Format($"已识别 {tags.Length} 个标签") : Localization.Text("标签");
        _detailState.IsVisible = _liveResults.ContainsKey(result.Path);
        _detailState.Text = !_detailState.IsVisible ? "" : _busy ? Localization.Format($"正在识别 · 当前 {tags.Length} 个标签") : Localization.Format($"部分结果 · {tags.Length} 个标签");
        foreach (var group in tags.GroupBy(tag => tag.Category))
        {
            var section = new StackPanel { Spacing = 6 };
            var groupHeading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
            groupHeading.Children.Add(Ui.Text(Localization.Text(group.Key), "heading"));
            var count = Ui.Text(group.Count().ToString(), "caption"); Localization.SetIsUserText(count, true);
            Grid.SetColumn(count, 1); groupHeading.Children.Add(count); section.Children.Add(groupHeading);
            var chips = new WrapPanel();
            foreach (var tag in group)
            {
                var label = Ui.Text(tag.Label + (_showScores.IsChecked == true ? " · " + Localization.Text(tag.ScoreKind switch
                    { "sample_peak" => "峰值", "cosine_similarity" => "相似度", _ => "分数" }) + $" {tag.Score:0.00}" : "")); Localization.SetIsUserText(label, true);
                var chip = new Button { Content = label, Padding = new(9, 5), Margin = new(0, 0, 6, 6), BorderThickness = new(1) };
                if (tag.Model == "manual") chip.Click += async (_, _) => await EditTagsAsync(result);
                else
                {
                    chip.Click += (_, _) => { SelectTrace(TagKey(tag)); _chartSection.IsExpanded = true; _chartSection.BringIntoView(); };
                    if (video)
                    {
                        var evidence = new MenuItem { Header = "查看达标采样…" }; evidence.Click += async (_, _) => await ShowEvidenceAsync(result, tag);
                        chip.ContextMenu = new ContextMenu { Items = { evidence } };
                    }
                    var kind = Localization.Text(tag.ScoreKind switch { "sample_peak" => "峰值", "sample_average" => "平均", "current_frame" => "当前画面", "cosine_similarity" => "相似度", _ => "分数" });
                    ToolTip.SetTip(chip, tag.Label + "\n" + ModelLabel(tag.Model) + " · " + kind + $" {tag.Score:0.000}");
                }
                chip.Bind(Button.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
                chip.Bind(Button.BorderBrushProperty, new DynamicResourceExtension("UiBorder")); chips.Children.Add(chip);
            }
            section.Children.Add(chips); _tagGroups.Children.Add(section);
        }
        var moderation = NsfwModeration.Evaluate(result, (double)(_threshold.Value ?? .4m));
        var details = new StackPanel { Spacing = 5 };
        details.Children.Add(Ui.Text(video
            ? Localization.Format($"采样 {result.SampledFrames} 帧 · 计算 {result.InferredFrames} 帧 · {result.Backend}")
            : Localization.Text("图片识别") + " · " + result.Backend, "caption"));
        if (result.FallbackReason is not null) details.Children.Add(Ui.Text(Localization.Text("已回退 CPU") + " · " + result.FallbackReason, "caption"));
        if (result.Scenes is { } scenes)
        {
            details.Children.Add(Ui.Text(Localization.Text("场景、照明与面部") + " · " + scenes.Backend, "caption"));
            if (scenes.FallbackReason is not null) details.Children.Add(Ui.Text(scenes.FallbackReason, "caption"));
        }
        UpdateNsfwBadge(moderation, video);
        if (_nsfwEvidence.Parent is Panel previous) previous.Children.Remove(_nsfwEvidence);
        if (_nsfwEvidence.Children.Count > 0) details.Children.Add(_nsfwEvidence);
        _details.Content = new ScrollViewer { Content = details, MaxHeight = 320 };
        _tagPanel.IsVisible = _tagGroups.Children.Count > 0;
        _details.IsVisible = true; UpdateActions();
    }
    private async Task RefreshSelectedPreviewAsync(double? seconds = null, bool debounce = false)
    {
        _previewRequest?.Cancel();
        if (_closed) return;
        var entry = _list.SelectedItem as MediaFileEntry;
        if (!BatchRename.PathComparer.Equals(_previewPath, entry?.Path))
        {
            _preview.Source = null; _previewBitmap?.Dispose(); _previewBitmap = null; _previewPath = entry?.Path; _previewSeconds = null;
            _preview.Width = 240; _preview.Height = entry is not null && VideoFormats.IsVideo(entry.Path) ? 135 : 240;
        }
        // Reserve the preview space while decoding; seeking within a file keeps its last frame visible.
        _preview.IsVisible = entry is not null;
        if (entry is null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _previewRequest = request;
        var acquired = false;
        try
        {
            if (debounce) await Task.Delay(90, request.Token);
            await _previewGate.WaitAsync(request.Token); acquired = true;
            request.Token.ThrowIfCancellationRequested();
            if (TryDisplayedResult(entry.Path, out var result)) MediaTagService.ValidateSource(result);
            var position = seconds ?? _positions.GetValueOrDefault(entry.Path);
            if (_previewBitmap is not null && _previewSeconds is { } shown && Math.Abs(shown - position) < .0001) return;
            var video = VideoFormats.IsVideo(entry.Path);
            var bytes = await _engine.Thumbnail(entry.Path, position, 480, video ? 300 : 512, request.Token, pad: false);
            if (_closed || request.IsCancellationRequested || _previewRequest != request) return;
            using var stream = new MemoryStream(bytes);
            var next = new Bitmap(stream); var previous = _previewBitmap;
            var scale = Math.Min(240d / next.PixelSize.Width, (video ? 150d : 256d) / next.PixelSize.Height);
            _preview.Width = next.PixelSize.Width * scale; _preview.Height = next.PixelSize.Height * scale;
            _previewBitmap = next; _preview.Source = next; _previewSeconds = position; previous?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (IOException error) { if (!_closed && _previewRequest == request) _status.Text = error.Message; }
        // A failed thumbnail does not block analysis or replace the tag result with an error.
        catch (Exception) { }
        finally
        {
            if (acquired) _previewGate.Release();
            if (_previewRequest == request) _previewRequest = null;
        }
    }
}
