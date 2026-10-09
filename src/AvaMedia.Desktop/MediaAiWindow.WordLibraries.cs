using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private sealed record ResultTag(string Label, string Category, double Score, string ScoreKind = "score", string Model = ModelCatalog.JoyTagId);
    private WordCandidate[] _libraryCandidates = [];
    private readonly CheckBox _onlyLibrary = new() { Content = "仅显示所选词库" };
    private readonly TextBlock _librarySummary = Ui.Text("", "caption");

    private void InitializeWordLibraries() => Opened += (_, _) => ReloadWordCandidates();
    private void ReloadWordCandidates()
    {
        try
        {
            _libraryCandidates = new WordLibraryStore().Resolve(WordLibraryTarget.JoyTag);
            _librarySummary.Text = Localization.Format($"词库候选 {_libraryCandidates.Length} 个");_librarySummary.IsVisible=_libraryCandidates.Length>0;
            _onlyLibrary.IsEnabled = _libraryCandidates.Length > 0;
            if (_libraryCandidates.Length == 0) _onlyLibrary.IsChecked = false;
        }
        catch (Exception error)
        {
            _libraryCandidates = []; _onlyLibrary.IsChecked = false; _onlyLibrary.IsEnabled = false;
            _librarySummary.Text = Localization.Text("词库读取失败：") + error.Message;
        }
    }
    private IEnumerable<ResultTag> ResultTags(MediaTagResult result, bool search = false)
    {
        var threshold = (double)(_threshold.Value ?? .4m);
        IEnumerable<ResultTag> tags;
        if (_onlyLibrary.IsChecked == true)
        {
            var scores = result.Scores.ToDictionary(score => score.Tag, score => MediaTagService.TagSignal(result, score), StringComparer.OrdinalIgnoreCase);
            tags = _libraryCandidates.Where(entry => entry.Tags.Length > 0 && entry.Tags.All(tag => scores.GetValueOrDefault(tag) >= threshold))
                .Select(entry => new ResultTag(entry.Label, entry.Category, entry.Tags.Min(tag => scores.GetValueOrDefault(tag)),
                    VideoFormats.IsVideo(result.Path) && entry.Tags.All(WordLibraryCatalog.UsesSamplePeak) ? "sample_peak" : "score"));
        }
        else tags = result.Scores.Where(score => MediaTagService.TagSignal(result, score) >= threshold)
            .Select(score => new ResultTag(WordLibraryCatalog.TagLabel(score.Tag), WordLibraryCatalog.TagCategory(score.Tag), MediaTagService.TagSignal(result, score),
                VideoFormats.IsVideo(result.Path) && WordLibraryCatalog.UsesSamplePeak(score.Tag) ? "sample_peak" : "score"));
        if (result.Scenes is { } scenes)
            tags = tags.Concat(scenes.Scores.Where(score => _onlyLibrary.IsChecked != true || _libraryCandidates.Any(entry => entry.Label == score.Label))
                .Select(score => new ResultTag(score.Label, score.Category, score.Similarity, "cosine_similarity", scenes.Model)));
        if(_editedTags.TryGetValue(result.Path,out var edited))tags=edited;
        var query = search ? _tagSearch.Text?.Trim() ?? "" : "";
        return tags.Where(tag => query.Length == 0 || tag.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => tag.Category.StartsWith("NSFW", StringComparison.Ordinal) ? 0 : tag.Category.StartsWith("场景", StringComparison.Ordinal) || tag.Category == "照明状态" ? 1 : 2)
            .ThenByDescending(tag => tag.Score).DistinctBy(tag => tag.Label, StringComparer.OrdinalIgnoreCase);
    }
}
