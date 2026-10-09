using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow : IClassificationCoverSource
{
    private readonly SemaphoreSlim _coverGate = new(2, 2);
    private readonly Dictionary<(string Path, double Seconds, int Width, long Length, DateTime Modified), byte[]> _coverCache = [];
    private readonly Queue<(string Path, double Seconds, int Width, long Length, DateTime Modified)> _coverOrder = [];
    private long _coverBytes;
    private readonly ClassificationCover _selectedCover = new() { Height = 180, ResolutionWidth = 640 };
    private readonly StackPanel _coverPanel = new() { Spacing = 6 };
    private double _coverPosition;
    private string? _coverPath;
    private bool _coverTouched;
    private string? _coverControlsKey;

    async Task<byte[]> IClassificationCoverSource.ReadClassificationCoverAsync(string path, double seconds, int width, CancellationToken ct)
    {
        if (_closed) throw new OperationCanceledException(ct);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _coverGate.WaitAsync(request.Token);
        try
        {
            request.Token.ThrowIfCancellationRequested();
            if (_results.TryGetValue(path, out var classified)) MediaTagService.ValidateSource(classified.Media);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException();
            var key = (path, seconds, width, info.Length, info.LastWriteTimeUtc);
            if (_coverCache.TryGetValue(key, out var cached)) return cached;
            var bytes = await _engine.Thumbnail(path, seconds, width, width * 9 / 16, request.Token, pad: false);
            request.Token.ThrowIfCancellationRequested();
            if (_coverCache.TryGetValue(key, out cached)) return cached;
            _coverCache[key] = bytes; _coverOrder.Enqueue(key); _coverBytes += bytes.Length;
            while (_coverOrder.Count > 96 || _coverBytes > 24 * 1024 * 1024)
                if (_coverCache.Remove(_coverOrder.Dequeue(), out var old)) _coverBytes -= old.Length;
            return bytes;
        }
        finally { _coverGate.Release(); }
    }

    private double CoverSeconds(MediaFileEntry entry) => _results.TryGetValue(entry.Path, out var result)
        ? result.Decisions.Select(decision => decision.Seconds).OfType<double>().FirstOrDefault() : 0;

    private void RenderCover(MediaFileEntry? entry)
    {
        if (entry is null)
        { _selectedCover.Path = null; _coverPanel.Children.Clear(); _coverPanel.IsVisible = false; _coverControlsKey = null; _coverPath = null; return; }
        _coverPanel.IsVisible = true;
        if (!BatchRename.PathComparer.Equals(_coverPath, entry.Path))
        { _coverPath = entry.Path; _coverTouched = false; }
        if (!_coverTouched) _coverPosition = CoverSeconds(entry);
        _selectedCover.Seconds = _coverPosition; _selectedCover.Path = entry.Path;
        var times = _results.TryGetValue(entry.Path, out var result) && VideoFormats.IsVideo(entry.Path)
            ? result.Decisions.SelectMany(decision => decision.Frames).Select(frame => frame.Seconds)
                .Concat(result.Media.Frames.Select(frame => frame.Seconds)).Distinct().Order().ToArray() : [];
        var key = entry.Path + "|" + string.Join(",", times.Select(time => time.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        if (_coverControlsKey == key) return;
        _coverControlsKey = key; _coverPanel.Children.Clear(); _coverPanel.Children.Add(_selectedCover);
        var actions = new WrapPanel();
        var open = Ui.Button(VideoFormats.IsVideo(entry.Path) ? "播放视频" : "打开图片", OpenSelected);
        open.Margin = new(0, 0, 6, 0); actions.Children.Add(open);
        if (times.Length > 0)
        {
            var timeline = new ComboBox { MinWidth = 104, ItemsSource = times.Select(time => $"{time:0.00}s").ToArray(),
                SelectedIndex = Math.Max(0, Array.FindIndex(times, time => Math.Abs(time - _coverPosition) < .001)) };
            ToolTip.SetTip(timeline, "查看视频采样画面");
            timeline.SelectionChanged += (_, _) =>
            { if (timeline.SelectedIndex >= 0) { _coverTouched = true; _coverPosition = times[timeline.SelectedIndex]; _selectedCover.Seconds = _coverPosition; } };
            actions.Children.Add(timeline);
        }
        _coverPanel.Children.Add(actions);
    }

    private void RenderDetails()
    {
        _details.Children.Clear();
        var entry = _files.SelectedItem as MediaFileEntry; RenderCover(entry);
        if (entry is null) { _details.Children.Add(Ui.Text("选择封面查看分类结果", "caption")); return; }
        var title = UserText(entry.Name, "settingsHeading"); title.MaxLines = 2; title.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; ToolTip.SetTip(title, entry.Path); _details.Children.Add(title);
        if (!_results.TryGetValue(entry.Path, out var result)) _details.Children.Add(Ui.Text(entry.Status, "caption"));
        foreach (var rule in _rules)
        {
            var decision = result?.Decisions.FirstOrDefault(item => item.RuleId == rule.Id);
            _details.Children.Add(UserText(rule.Name, "settingsHeading"));
            var categories = rule.Categories;
            var choice = new ComboBox { ItemsSource = new[] { Localization.Text("待确认") }.Concat(categories.Select(category => category.Name)).ToArray(),
                SelectedIndex = decision?.CategoryId is { } id ? Array.FindIndex(categories, category => category.Id == id) + 1 : 0, IsEnabled = !_busy };
            Localization.SetIsUserText(choice, true);
            choice.SelectionChanged += async (_, _) =>
            {
                if (_busy || choice.SelectedIndex < 0) return;
                await GuardAsync(() => PlaceInBasketAsync(entry, rule, choice.SelectedIndex == 0 ? null : categories[choice.SelectedIndex - 1]));
            };
            _details.Children.Add(choice);
            if (decision is null) continue;
            _details.Children.Add(Ui.Text(decision.Manual ? "人工确认" : decision.Evidence, "caption"));
            if (decision.Frames.Count > 0)
            {
                var scores = new StackPanel { Spacing = 4 };
                scores.Children.Add(Ui.Text(Localization.Format($"采样一致率 {decision.Agreement:P0} · {decision.Frames.Count} 帧"), "caption"));
                foreach (var score in decision.Scores.OrderByDescending(score => score.Similarity))
                    scores.Children.Add(UserText($"{score.Name}  {score.Similarity:0.000}  ·  {score.MatchedFrames}/{decision.Frames.Count}"));
                _details.Children.Add(new Expander { Header = "匹配详情", Content = scores, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
            if (rule.Id == "age-appearance") _details.Children.Add(Ui.Text("外观年龄段供参考，请人工核对。", "caption"));
            if (decision.Manual)
                _details.Children.Add(Ui.Button("恢复自动分类", () => RestoreAutomatic(entry, rule)));
        }
        if (result is not null)
        {
            _details.Children.Add(Ui.Text("将放入", "settingsHeading"));
            _details.Children.Add(UserText(DestinationDisplay(entry, result)));
            if (!string.IsNullOrWhiteSpace(entry.NewName)) _details.Children.Add(Ui.Text("目录预览已核对", "caption"));
            _details.Children.Add(Ui.Text("自动标签", "settingsHeading"));
            _details.Children.Add(UserText(result.Tags.Count == 0 ? Localization.Text("暂无标签") : string.Join(" · ", result.Tags)));
        }
        _details.Children.Add(UserText(entry.Path));
    }

    private string DestinationDisplay(MediaFileEntry entry, FolderClassifiedFile result)
    {
        if (!string.IsNullOrWhiteSpace(entry.NewName)) return entry.NewName;
        if (string.IsNullOrWhiteSpace(_output.Text)) return string.Join(" / ", result.Decisions.Select(decision => decision.CategoryName));
        try { return FolderOrganization.DestinationFolder(result, _output.Text, _splitTypes.IsChecked == true); }
        catch (Exception error) when (error is ArgumentException or IOException) { return error.Message; }
    }

    private static TextBlock UserText(string text, string style = "caption")
    { var block = Ui.Text(text, style); Localization.SetIsUserText(block, true); return block; }
}
