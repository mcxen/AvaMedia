using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class RenameWindow
{
    private WordCandidate[] _semanticCandidates = [];
    private string? _semanticLibraryError;
    private readonly TextBlock _semanticLibrarySummary = Ui.Text("尚未选择词库候选", "caption");

    private void InitializeSemanticWordLibraries()
    {
        _semanticParameters.Children.Add(Ui.Button("从词库添加…", async () =>
        {
            var picker = new WordLibraryWindow(WordLibraryTarget.Semantic);
            await picker.ShowDialog(this);
            if (_closed) return;
            ReloadSemanticCandidates(); ClearSemanticMatches();
        }));
        _semanticParameters.Children.Add(_semanticLibrarySummary);

        Opened += (_, _) => ReloadSemanticCandidates();
    }
    private void ReloadSemanticCandidates()
    {
        try
        {
            _semanticCandidates = new WordLibraryStore().Resolve(WordLibraryTarget.Semantic);
            _semanticLibraryError = null;
            _semanticLibrarySummary.Text = Localization.Format($"词库候选 {_semanticCandidates.Length} 个");_semanticLibrarySummary.IsVisible=_semanticCandidates.Length>0;
        }
        catch (Exception error) { _semanticLibraryError = error.Message; _semanticCandidates = []; _semanticLibrarySummary.Text = "词库读取失败：" + error.Message; }
    }
    private SemanticKeyword[] SemanticCandidates()
    {
        ReloadSemanticCandidates();
        if (_semanticLibraryError is not null) throw new InvalidDataException("词库读取失败：" + _semanticLibraryError);
        var manual = string.IsNullOrWhiteSpace(_keywords.Text) ? [] : MediaKeywordMatcher.ParseKeywords(_keywords.Text);
        var candidates = manual.Concat(_semanticCandidates.Select(entry => new SemanticKeyword(entry.Label, entry.Description)))
            .DistinctBy(keyword => keyword.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length is < 1 or > WordLibraryCatalog.MaximumCandidates) throw new ArgumentException("请选择词库或输入 1–20000 个候选词。");
        return candidates;
    }
}
