using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private WordCandidate[] _libraryCandidates = [];
    private string? _libraryError;
    private readonly TextBlock _librarySummary = Ui.Text("尚未选择词库候选", "caption");

    private void InitializeWordLibraries()
    {
        _parameters.Children.Add(Ui.Button("选择词库 / 类别…", async () =>
        {
            var picker = new WordLibraryWindow(WordLibraryTarget.JoyTag);
            await picker.ShowDialog(this);
            if (_closed) return;
            ReloadWordCandidates(); InvalidatePlan();
        }));
        _parameters.Children.Add(_librarySummary);
        _parameters.Children.Add(Ui.Text("在「选项 → 词库管理」编辑；下方可补充关键词。", "caption"));
        Opened += (_, _) => ReloadWordCandidates();
    }
    private void ReloadWordCandidates()
    {
        try
        {
            _libraryCandidates = new WordLibraryStore().Resolve(WordLibraryTarget.JoyTag);
            _libraryError = null;
            _librarySummary.Text = Localization.Format($"词库候选 {_libraryCandidates.Length} 个 · 使用全部达标标签");
        }
        catch (Exception error) { _libraryError = error.Message; _libraryCandidates = []; _librarySummary.Text = "词库读取失败：" + error.Message; }
    }
    private MediaTagQuery[] CandidateQueries(IEnumerable<string> vocabulary)
    {
        ReloadWordCandidates();
        if (_libraryError is not null) throw new InvalidDataException("词库读取失败：" + _libraryError);
        var manual = MediaTagService.ParseQueries(_keywords.Text ?? "", vocabulary);
        var candidates = manual.Concat(_libraryCandidates.Select(entry => new MediaTagQuery(entry.Label, entry.Tags)))
            .DistinctBy(query => query.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length > WordLibraryCatalog.MaximumCandidates) throw new ArgumentException("最多选择 20000 个候选词。");
        return candidates;
    }
}
