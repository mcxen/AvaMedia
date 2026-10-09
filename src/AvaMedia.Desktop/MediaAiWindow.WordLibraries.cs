using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private sealed record ResultTag(string Label, string Category, double Score, string ScoreKind = "score", string Model = ModelCatalog.JoyTagId, string[]? RawTags = null);
    private WordCandidate[] _libraryCandidates = [];
    private readonly CheckBox _onlyLibrary = new() { Content = "仅显示所选词库" };
    private readonly TextBlock _librarySummary = Ui.Text("", "caption");
    private WordCandidate[] SemanticLibraryCandidates => _libraryCandidates.Where(entry => !entry.Supports(WordLibraryTarget.JoyTag)).ToArray();
    private bool NeedsSemanticModel => _sceneTags.IsChecked == true || SemanticLibraryCandidates.Length > 0;

    private void InitializeWordLibraries() => Opened += (_, _) => ReloadWordCandidates();
    private void ReloadWordCandidates()
    {
        try
        {
            _libraryCandidates = new WordLibraryStore().Resolve(WordLibraryTarget.JoyTag, includeSemantic: ModelCatalog.Find(ModelCatalog.EmbeddingId).Supported);
            var semanticCount = SemanticLibraryCandidates.Length;
            _librarySummary.Text = Localization.Format($"已选 {_libraryCandidates.Length} 个标签 · 语义候选 {semanticCount} 个");
            _librarySummary.IsVisible = _libraryCandidates.Length > 0;
            _onlyLibrary.IsEnabled = _libraryCandidates.Length > 0;
            if (_libraryCandidates.Length == 0) _onlyLibrary.IsChecked = false;
        }
        catch (Exception error)
        {
            _libraryCandidates = []; _onlyLibrary.IsChecked = false; _onlyLibrary.IsEnabled = false;
            _librarySummary.Text = Localization.Text("词库读取失败：") + error.Message;
            _librarySummary.IsVisible = true;
        }
    }
    private async Task OpenTagGroupsAsync()
    {
        await new WordLibraryWindow(WordLibraryTarget.JoyTag, includeSemantic: ModelCatalog.Find(ModelCatalog.EmbeddingId).Supported).ShowDialog(this);
        if (_closed) return;
        ReloadWordCandidates(); RefreshDisplayedResults();
        if (_results.Values.Any(result => SemanticLibraryCandidates.Any(entry =>
            result.Scenes?.Frames.Any(frame => frame.Candidates.Any(candidate => candidate.Label.Equals(entry.Label, StringComparison.OrdinalIgnoreCase))) != true)))
            _status.Text = Localization.Text("新选的语义标签需要重新分析");
        await RefreshModelAsync();
    }
    private IEnumerable<ResultTag> JoyCandidates(MediaTagResult result)
    {
        var scores = result.Scores.ToDictionary(score => score.Tag, score => JoyValue(result, score), StringComparer.OrdinalIgnoreCase);
        var selected = _libraryCandidates.Where(entry => entry.Tags.Length > 0 && entry.Tags.All(scores.ContainsKey))
            .Select(entry => new ResultTag(entry.Label, entry.Category, entry.Tags.Min(tag => scores[tag]),
                JoyScoreKind(result, entry.Tags), RawTags: entry.Tags));
        return _onlyLibrary.IsChecked == true ? selected : selected.Concat(result.Scores
            .Select(score => new ResultTag(WordLibraryCatalog.TagLabel(score.Tag), WordLibraryCatalog.TagCategory(score.Tag), JoyValue(result, score),
                JoyScoreKind(result, [score.Tag]), RawTags: [score.Tag])));
    }
    private IEnumerable<ResultTag> ResultTags(MediaTagResult result, bool search = false)
    {
        var threshold = (double)(_threshold.Value ?? .4m);
        IEnumerable<ResultTag> tags = JoyCandidates(result).Where(tag => tag.Score >= threshold);
        tags = tags.Concat(SceneCandidates(result).Where(tag => SceneQualifies(result, tag)));
        if(_editedTags.TryGetValue(result.Path,out var edited))tags=edited;
        var query = search ? _tagSearch.Text?.Trim() ?? "" : "";
        return tags.Where(tag => (!search || ScopeMatches(tag)) && (query.Length == 0 || tag.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(tag => tag.Category.StartsWith("NSFW", StringComparison.Ordinal) ? 0 : tag.Category.StartsWith("场景", StringComparison.Ordinal) || tag.Category == "照明状态" ? 1 : 2)
            .ThenByDescending(tag => tag.Score).DistinctBy(tag => tag.Label, StringComparer.OrdinalIgnoreCase);
    }
}
