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
        var result = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 10 };
        var heading = new Grid { ColumnDefinitions = new("*,170"), ColumnSpacing = 12 };
        var title = new StackPanel { Spacing = 5 }; title.Children.Add(_detailTitle); title.Children.Add(_detailState); heading.Children.Add(title);
        Grid.SetColumn(_preview, 1); heading.Children.Add(_preview); result.Children.Add(heading);
        Grid.SetRow(_tagSearch, 1); result.Children.Add(_tagSearch);
        var tags = new ScrollViewer { Content = _tagGroups, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(tags, 2); result.Children.Add(tags); Grid.SetRow(_details, 3); result.Children.Add(_details); return result;
    }
    private void RenderSelectedResult()
    {
        if (_closed) return;
        _tagGroups.Children.Clear(); _details.IsVisible = false;
        var entry = _list.SelectedItem as MediaFileEntry;
        _detailTitle.Text = entry?.Name ?? Localization.Text("识别结果"); Localization.SetIsUserText(_detailTitle, true);
        _tagSearch.IsVisible = entry is not null && _results.ContainsKey(entry.Path);
        if (entry is null || !_results.TryGetValue(entry.Path, out var result))
        {
            _detailState.Text = entry?.Status ?? Localization.Text("尚未添加文件");
            if (entry?.Status == Localization.Text("失败") && entry.Details.Length > 0) _tagGroups.Children.Add(Ui.Text(entry.Details, "error"));
            UpdateActions(); return;
        }
        var tags = ResultTags(result, search: true).ToArray();
        _detailState.Text = Localization.Format($"识别到 {tags.Length} 个标签");
        if (tags.Length == 0) _tagGroups.Children.Add(Ui.Text(string.IsNullOrWhiteSpace(_tagSearch.Text) ? "未找到达标标签" : "未找到匹配标签", "caption"));
        foreach (var group in tags.GroupBy(tag => tag.Category).OrderBy(group => group.Key == "其他标签" ? 1 : 0))
        {
            var section = new StackPanel { Spacing = 6 }; section.Children.Add(Ui.Text(Localization.Text(group.Key), "heading"));
            var chips = new WrapPanel();
            foreach (var tag in group)
            {
                var label = Ui.Text(tag.Label + (_showScores.IsChecked == true ? $" · {tag.Score:0.00}" : "")); Localization.SetIsUserText(label, true);
                var chip = new Border { Child = label, Padding = new(9, 5), Margin = new(0, 0, 6, 6), BorderThickness = new(1) };
                chip.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
                chip.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiBorder")); chips.Children.Add(chip);
            }
            section.Children.Add(chips); _tagGroups.Children.Add(section);
        }
        var moderation = NsfwModeration.Evaluate(result, (double)(_threshold.Value ?? .4m));
        var details = new StackPanel { Spacing = 5 };
        details.Children.Add(Ui.Text(Localization.Format($"采样 {result.SampledFrames} 帧 · 计算 {result.InferredFrames} 帧 · {result.Backend}"), "caption"));
        if (result.FallbackReason is not null) details.Children.Add(Ui.Text(Localization.Text("已回退 CPU") + " · " + result.FallbackReason, "caption"));
        details.Children.Add(Ui.Text(NsfwStateText(moderation.State), "caption"));
        if (moderation.Evidence.Count > 0) details.Children.Add(Ui.Text(string.Join(" · ", moderation.Evidence.Select(item => $"{item.Label} {item.Signal:0.00}")), "caption"));
        else details.Children.Add(Ui.Text("未检出风险标签不代表安全。", "caption"));
        if (moderation.State == NsfwSignalState.Suspected) _tagGroups.Children.Insert(0, Ui.Text("疑似 NSFW", "error"));
        _details.Content = new ScrollViewer { Content = details, MaxHeight = 140 };
        _details.IsVisible = true; UpdateActions();
    }
    private async Task RefreshSelectedPreviewAsync()
    {
        _previewRequest?.Cancel();
        _preview.Source = null; _preview.IsVisible = false; _previewBitmap?.Dispose(); _previewBitmap = null;
        if (_closed || _list.SelectedItem is not MediaFileEntry entry) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _previewRequest = request;
        try
        {
            var bytes = await _engine.Thumbnail(entry.Path, 0, 220, 140, request.Token, pad: false);
            if (_closed || request.IsCancellationRequested || _previewRequest != request) return;
            using var stream = new MemoryStream(bytes); _previewBitmap = new Bitmap(stream); _preview.Source = _previewBitmap; _preview.IsVisible = true;
        }
        // A failed thumbnail does not block analysis or replace the tag result with an error.
        catch (Exception) { }
        finally { if (_previewRequest == request) _previewRequest = null; }
    }
}
