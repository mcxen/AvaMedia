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
    private readonly TextBox _tagSearch = new() { Watermark = "查找标签", Name = "MediaAiTagSearch" };
    private readonly StackPanel _tagGroups = new() { Spacing = 14 };
    private readonly Image _preview = new() { Height = 140, Stretch = Stretch.Uniform, IsVisible = false };
    private readonly Expander _details = new() { Header = "识别详情", IsVisible = false };
    private Bitmap? _previewBitmap;
    private CancellationTokenSource? _previewRequest;

    private Control BuildResultPane()
    {
        var result = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 10 };
        var content = new StackPanel { Spacing = 14 };
        var heading = new Grid { ColumnDefinitions = new("*,190"), ColumnSpacing = 12 };
        var title = new StackPanel { Spacing = 5 }; title.Children.Add(_detailTitle); title.Children.Add(_detailState); heading.Children.Add(title);
        Grid.SetColumn(_preview, 1); heading.Children.Add(_preview); content.Children.Add(heading);
        content.Children.Add(BuildWorkbench()); content.Children.Add(BuildCharts()); content.Children.Add(_tagSearch);
        content.Children.Add(_tagGroups); content.Children.Add(_details);
        result.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var actions = new WrapPanel();
        foreach (var button in new[] { _saveTxt, _copy, _export, _rename, _undo }) { button.Margin = new(0, 0, 8, 6); actions.Children.Add(button); }
        Grid.SetRow(actions, 1); result.Children.Add(actions); return result;
    }
    private void RenderSelectedResult()
    {
        if (_closed) return;
        _tagGroups.Children.Clear(); _details.IsVisible = false;
        var entry = _list.SelectedItem as MediaFileEntry;
        _detailTitle.Text = entry?.Name ?? Localization.Text("识别结果"); Localization.SetIsUserText(_detailTitle, true);
        _tagSearch.IsVisible = entry is not null && TryDisplayedResult(entry.Path, out _);
        if (entry is null || !TryDisplayedResult(entry.Path, out var result))
        {
            _detailState.Text = entry?.Status ?? Localization.Text("尚未添加文件");
            if (entry?.Status == Localization.Text("失败") && entry.Details.Length > 0) _tagGroups.Children.Add(Ui.Text(entry.Details, "error"));
            RenderCharts(null); UpdateActions(); return;
        }
        RenderCharts(result);
        var edits=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        edits.Children.Add(Ui.Button("编辑标签…",async()=>await EditTagsAsync(result)));
        if(_editedTags.ContainsKey(result.Path))edits.Children.Add(Ui.Button("恢复识别标签",()=>{_editedTags.Remove(result.Path);RefreshDisplayedResults();}));
        if (_results.ContainsKey(result.Path)) _tagGroups.Children.Add(edits);
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
    private async Task RefreshSelectedPreviewAsync(double? seconds = null)
    {
        _previewRequest?.Cancel();
        _preview.Source = null; _preview.IsVisible = false; _previewBitmap?.Dispose(); _previewBitmap = null;
        if (_closed || _list.SelectedItem is not MediaFileEntry entry) return;
        if (TryDisplayedResult(entry.Path, out var result))
        {
            try { MediaTagService.ValidateSource(result); }
            catch (IOException error) { _status.Text = error.Message; return; }
        }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _previewRequest = request;
        try
        {
            var bytes = await _engine.Thumbnail(entry.Path, seconds ?? _positions.GetValueOrDefault(entry.Path), 280, 140, request.Token, pad: false);
            if (_closed || request.IsCancellationRequested || _previewRequest != request) return;
            using var stream = new MemoryStream(bytes); _previewBitmap = new Bitmap(stream); _preview.Source = _previewBitmap; _preview.IsVisible = true;
        }
        // A failed thumbnail does not block analysis or replace the tag result with an error.
        catch (Exception) { }
        finally { if (_previewRequest == request) _previewRequest = null; }
    }
}
