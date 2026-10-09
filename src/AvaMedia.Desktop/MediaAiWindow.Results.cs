using Avalonia;
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
    private readonly TextBlock _detailState = Ui.Text("尚未添加文件", "caption");
    private readonly TextBox _tagSearch = new() { Watermark = "查找标签", Name = "MediaAiTagSearch", Width = 240 };
    private readonly Button _editTags = new() { Content = "编辑标签…", IsVisible = false };
    private readonly Button _restoreTags = new() { Content = "恢复识别标签", IsVisible = false };
    private readonly Border _tagPanel = ChartPanel();
    private readonly StackPanel _tagGroups = new() { Spacing = 10 };
    private readonly Image _preview = new() { Width = 224, Height = 126, Stretch = Stretch.Uniform, IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly Expander _details = new() { Header = "识别详情", IsVisible = false };
    private Bitmap? _previewBitmap;
    private CancellationTokenSource? _previewRequest;
    private string? _previewPath;
    private double? _previewSeconds;
    private readonly SemaphoreSlim _previewGate = new(1, 1);

    private Control BuildResultPane()
    {
        var content = new StackPanel { Spacing = 10 };
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        heading.Children.Add(_preview);
        _detailTitle.MaxLines = 2; _detailTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        var title = new StackPanel { Spacing = 6 };
        title.Children.Add(_detailTitle); title.Children.Add(_detailState);
        _sampleSummary.Classes.Add("time");
        title.Children.Add(WorkbenchActions(_sampleSummary, _playSample, _followLive));
        title.Children.Add(WorkbenchActions(_saveTxt, _export, _rename, _undo));
        Grid.SetColumn(title, 1); heading.Children.Add(title); content.Children.Add(ChartPanel(heading));
        content.Children.Add(ChartPanel(BuildWorkbench())); content.Children.Add(BuildCharts());
        var tags = new StackPanel { Spacing = 6 };
        tags.Children.Add(WorkbenchActions(_tagSearch, _editTags, _restoreTags, _copy));
        tags.Children.Add(_tagGroups); tags.Children.Add(_details); _tagPanel.Child = tags; content.Children.Add(_tagPanel);
        _editTags.Click += async (_, _) =>
        {
            if (_list.SelectedItem is MediaFileEntry entry && _results.TryGetValue(entry.Path, out var result)) await EditTagsAsync(result);
        };
        _restoreTags.Click += (_, _) =>
        {
            if (_list.SelectedItem is MediaFileEntry entry) { _editedTags.Remove(entry.Path); RefreshDisplayedResults(); }
        };
        return new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
    private void RenderSelectedResult()
    {
        if (_closed) return;
        _tagGroups.Children.Clear(); _details.IsVisible = false;
        var entry = _list.SelectedItem as MediaFileEntry;
        _detailTitle.Text = entry?.Name ?? Localization.Text("识别结果"); Localization.SetIsUserText(_detailTitle, true);
        ToolTip.SetTip(_detailTitle, entry?.Path);
        _tagSearch.IsVisible = entry is not null && TryDisplayedResult(entry.Path, out _);
        _tagPanel.IsVisible = _tagSearch.IsVisible || entry?.Status == Localization.Text("失败") && entry.Details.Length > 0;
        _editTags.IsVisible = entry is not null && _results.ContainsKey(entry.Path);
        _restoreTags.IsVisible = _editTags.IsVisible && _editedTags.ContainsKey(entry!.Path);
        _followLive.IsVisible = _playSample.IsVisible = entry is not null && VideoFormats.IsVideo(entry.Path);
        if (entry is null || !TryDisplayedResult(entry.Path, out var result))
        {
            _detailState.Text = entry?.Status ?? Localization.Text("尚未添加文件");
            if (entry?.Status == Localization.Text("失败") && entry.Details.Length > 0) _tagGroups.Children.Add(Ui.Text(entry.Details, "error"));
            RenderCharts(null); UpdateActions(); return;
        }
        RenderCharts(result);
        if (!_busy && _liveResults.ContainsKey(result.Path) && entry.Details.Length > 0 && entry.Status == Localization.Text("失败")) _tagGroups.Children.Add(Ui.Text(entry.Details, "error"));
        if (result.SceneError is not null) _tagGroups.Children.Add(Ui.Text(Localization.Text("语义识别失败：") + result.SceneError, "error"));
        var tags = ResultTags(result, search: true).ToArray();
        _detailState.Text = _liveResults.ContainsKey(result.Path) ? (_busy ? Localization.Format($"正在识别 · 当前 {tags.Length} 个标签") : Localization.Format($"部分结果 · {tags.Length} 个标签")) : Localization.Format($"识别到 {tags.Length} 个标签");
        if (tags.Length == 0) _tagGroups.Children.Add(Ui.Text(string.IsNullOrWhiteSpace(_tagSearch.Text) ? "未找到达标标签" : "未找到匹配标签", "caption"));
        foreach (var group in tags.GroupBy(tag => tag.Category))
        {
            var section = new StackPanel { Spacing = 6 }; section.Children.Add(Ui.Text(Localization.Text(group.Key), "heading"));
            var chips = new WrapPanel();
            foreach (var tag in group)
            {
                var label = Ui.Text(tag.Label + (_showScores.IsChecked == true ? " · " + Localization.Text(tag.ScoreKind switch
                    { "sample_peak" => "峰值", "cosine_similarity" => "相似度", _ => "分数" }) + $" {tag.Score:0.00}" : "")); Localization.SetIsUserText(label, true);
                var chip = new Button { Content = label, Padding = new(9, 5), Margin = new(0, 0, 6, 6), BorderThickness = new(1) };
                chip.Click += (_, _) => SelectTrace(TagKey(tag));
                var evidence = new MenuItem { Header = "查看达标采样…" }; evidence.Click += async (_, _) => await ShowEvidenceAsync(result, tag);
                chip.ContextMenu = new ContextMenu { Items = { evidence } };
                chip.Bind(Button.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
                chip.Bind(Button.BorderBrushProperty, new DynamicResourceExtension("UiBorder")); chips.Children.Add(chip);
            }
            section.Children.Add(chips); _tagGroups.Children.Add(section);
        }
        var moderation = NsfwModeration.Evaluate(result, (double)(_threshold.Value ?? .4m));
        var details = new StackPanel { Spacing = 5 };
        details.Children.Add(Ui.Text(Localization.Format($"采样 {result.SampledFrames} 帧 · 计算 {result.InferredFrames} 帧 · {result.Backend}"), "caption"));
        if (result.FallbackReason is not null) details.Children.Add(Ui.Text(Localization.Text("已回退 CPU") + " · " + result.FallbackReason, "caption"));
        if (result.Scenes is { } scenes)
        {
            details.Children.Add(Ui.Text(Localization.Text("场景、照明与面部") + " · " + scenes.Backend, "caption"));
            if (scenes.FallbackReason is not null) details.Children.Add(Ui.Text(scenes.FallbackReason, "caption"));
        }
        details.Children.Add(Ui.Text(NsfwStateText(moderation.State), "caption"));
        if (moderation.Evidence.Count > 0) details.Children.Add(Ui.Text(string.Join(" · ", moderation.Evidence.Select(item => $"{item.Label} {item.Signal:0.00}")), "caption"));
        if (moderation.State == NsfwSignalState.Suspected) _tagGroups.Children.Insert(0, Ui.Text("疑似 NSFW", "error"));
        _details.Content = new ScrollViewer { Content = details, MaxHeight = 140 };
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
            var bytes = await _engine.Thumbnail(entry.Path, position, 280, 140, request.Token, pad: false);
            if (_closed || request.IsCancellationRequested || _previewRequest != request) return;
            using var stream = new MemoryStream(bytes);
            var next = new Bitmap(stream); var previous = _previewBitmap;
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
