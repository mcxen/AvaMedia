using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private sealed record ResultTag(string Label, string Category, double Score, string ScoreKind = "score", string Model = ModelCatalog.JoyTagId, string[]? RawTags = null);
    private WordCandidate[] _libraryCandidates = [];
    private HashSet<string> _privateLibraryLabels = [];
    private readonly CheckBox _onlyLibrary = new() { Content = "仅显示所选词库" };
    private readonly TextBlock _librarySummary = Ui.Text("", "caption");
    private WordCandidate[] SemanticLibraryCandidates => _libraryCandidates.Where(entry => !entry.Supports(WordLibraryTarget.JoyTag)).ToArray();
    private bool NeedsSemanticModel => _sceneTags.IsChecked == true || SemanticLibraryCandidates.Length > 0;

    private void InitializeWordLibraries() => Opened += (_, _) => { if (_taskJobs.Count == 0) ReloadWordCandidates(); };
    private void ReloadWordCandidates()
    {
        try
        {
            var candidates = new WordLibraryStore().Resolve(WordLibraryTarget.JoyTag, includeSemantic: ModelCatalog.Find(ModelCatalog.EmbeddingId).Supported);
            _privateLibraryLabels = candidates.Where(MediaPrivacy.IsSensitive).Select(entry => entry.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _libraryCandidates = candidates.Where(entry => _settings.EnableNsfwContent || !MediaPrivacy.IsSensitive(entry)).ToArray();
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
        await new WordLibraryWindow(WordLibraryTarget.JoyTag, includeSemantic: ModelCatalog.Find(ModelCatalog.EmbeddingId).Supported, settings: _settings).ShowDialog(this);
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
            .Select(entry => new ResultTag(WordLibraryCatalog.CandidateLabel(entry), WordLibraryCatalog.CandidateCategory(entry), entry.Tags.Min(tag => scores[tag]),
                JoyScoreKind(result, entry.Tags), RawTags: entry.Tags));
        return _onlyLibrary.IsChecked == true ? selected : selected.Concat(result.Scores
            .Where(score => _realPeople.IsChecked != true || WordLibraryCatalog.RealPeopleTags.Contains(score.Tag))
            .Select(score => new ResultTag(WordLibraryCatalog.TagLabel(score.Tag), WordLibraryCatalog.TagCategory(score.Tag), JoyValue(result, score),
                JoyScoreKind(result, [score.Tag]), RawTags: [score.Tag])));
    }
    private bool TagQualifies(MediaTagResult result, ResultTag tag) => double.IsFinite(tag.Score)
        && (tag.Model == ModelCatalog.EmbeddingId ? SceneQualifies(result, tag) : tag.Score >= (double)(_threshold.Value ?? .4m));
    private IEnumerable<ResultTag> DetectedTags(MediaTagResult result, string? model = null)
    {
        IEnumerable<ResultTag> tags = JoyCandidates(result).Concat(SceneCandidates(result)).Where(tag => TagQualifies(result, tag));
        if(_editedTags.TryGetValue(result.Path,out var edited))tags=edited;
        if (model is not null) tags = tags.Where(tag => tag.Model == model);
        return MediaTagText.NormalizeLabels(tags.Where(tag => _settings.EnableNsfwContent || !MediaPrivacy.IsSensitiveLabel(tag.Label, tag.Category, tag.RawTags))
            .Select(tag => new MediaTagTextLabel(tag.Label, tag.Category, tag.Score, tag.ScoreKind, tag.Model, tag.RawTags ?? [])))
            .Select(tag => new ResultTag(tag.Label, tag.Category, tag.Score, tag.ScoreKind, tag.Model, tag.Tags));
    }
    private IEnumerable<ResultTag> FilterResultTags(IEnumerable<ResultTag> tags, bool search)
    {
        var query = search ? _tagSearch.Text?.Trim() ?? "" : "";
        return tags.Where(tag => (!search || ScopeMatches(tag)) && (query.Length == 0 || tag.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase) || tag.RawTags?.Any(raw => raw.Contains(query, StringComparison.OrdinalIgnoreCase)) == true));
    }
    private IEnumerable<ResultTag> ResultTags(MediaTagResult result, bool search = false)
    {
        var tags = FilterResultTags(DetectedTags(result), search);
        return search && _tagSort.SelectedIndex == 1
            ? tags.OrderBy(tag => MediaTagText.CategoryOrder(tag.Category)).ThenBy(tag => tag.Category, StringComparer.Ordinal).ThenBy(tag => tag.Label, StringComparer.OrdinalIgnoreCase)
            : tags;
    }
}
