using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpWordLibraryInfo(string Id, string Name, string Source, bool BuiltIn, int Entries);
public sealed record McpWordPage(WordCandidate[] Entries, int Total, int Offset, int? NextOffset);
public sealed record McpWordPreferences(WordLibraryTarget? Target, SelectedWord[] Selection, WordLibraryView? View);

public sealed partial class McpTools
{
    private bool VisibleWord(WordCandidate entry) => PrivateEnabled || !MediaPrivacy.IsSensitiveLabel(entry.Label, entry.Category, entry.Tags);

    [McpServerTool(Name = "avamedia_get_word_preferences", Title = "读取词库选择设置", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read native JoyTag/Semantic word selections and remembered library/category view. target omitted reads the management view. Privacy filters selected labels; does not load models.")]
    public McpWordPreferences GetWordPreferences(WordLibraryTarget? target = null)
    {
        if (target is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("词库目标无效。");
        var store = new WordLibraryStore();
        var visible = store.Libraries().SelectMany(library => library.Entries.Where(VisibleWord).Select(entry => new SelectedWord(library.Id, entry.Label))).ToHashSet();
        return new(target, target is null ? [] : store.Selection(target.Value).Where(visible.Contains).ToArray(), store.View(target));
    }

    [McpServerTool(Name = "avamedia_set_word_preferences", Title = "更新词库选择设置", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Update native word-library selections/view without UI. selection omitted preserves it, [] clears it; a target JoyTag/Semantic is required for selections. view selects a library/category; clearView explicitly clears remembered view. target omitted addresses management view. Rejects unavailable/private/incompatible words and unknown library/category IDs. Existing task snapshots stay unchanged.")]
    public McpWordPreferences SetWordPreferences(WordLibraryTarget? target = null, SelectedWord[]? selection = null, WordLibraryView? view = null, bool clearView = false)
    {
        if (target is { } kind && !Enum.IsDefined(kind) || selection is not null && target is null || clearView && view is not null)
            throw new ArgumentException("词库目标或视图参数冲突。");
        var store = new WordLibraryStore(); var libraries = store.Libraries();
        if (view is not null && !libraries.Any(library => library.Id == view.LibraryId
            && (view.Category.Length == 0 || library.Entries.Any(entry => VisibleWord(entry) && entry.Category == view.Category))))
            throw new ArgumentException("词库或类别不可用。");
        if (selection is not null)
        {
            var allowed = libraries.SelectMany(library => library.Entries.Where(entry => VisibleWord(entry) && entry.Supports(target!.Value))
                .Select(entry => new SelectedWord(library.Id, entry.Label))).ToHashSet();
            if (selection.Any(word => !allowed.Contains(word))) throw new ArgumentException("候选词不存在、不可用或与目标模型不兼容。");
            store.SaveSelection(target!.Value, selection, view);
        }
        else if (view is not null) store.SaveView(target, view);
        if (clearView) store.ClearView(target);
        return GetWordPreferences(target);
    }

    [McpServerTool(Name = "avamedia_list_word_libraries", Title = "标签词库目录", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List native built-in/custom label libraries and visible entry counts. Uses the same WordLibraryStore as desktop; privacy filtering applies. read_word_library pages actual candidates for mediaTag.libraryCandidates or analysis.semanticCandidates.")]
    public McpWordLibraryInfo[] ListWordLibraries() => new WordLibraryStore().Libraries()
        .Select(library => new McpWordLibraryInfo(library.Id, library.Name, library.Source, library.BuiltIn, library.Entries.Count(VisibleWord))).ToArray();

    [McpServerTool(Name = "avamedia_read_word_library", Title = "读取标签候选词", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a native label library in pages, optionally filter by supported target JoyTag/Semantic and search label/category/description. Returns actual WordCandidate objects for fine-grained per-task AI candidate control. Built-in libraries remain immutable.")]
    public McpWordPage ReadWordLibrary(string libraryId, WordLibraryTarget? target = null, string? search = null, int offset = 0, int limit = 100)
    {
        Page(offset, limit);
        if (target is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("词库目标无效。");
        var library = new WordLibraryStore().Libraries().FirstOrDefault(library => library.Id == libraryId) ?? throw new ArgumentException("词库不存在。");
        var entries = library.Entries.Where(entry => VisibleWord(entry) && (target is null || entry.Supports(target.Value))
            && (string.IsNullOrEmpty(search) || (entry.Label + " " + entry.Category + " " + entry.Description).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new(entries.Skip(offset).Take(limit).ToArray(), entries.Length, offset, offset + limit < entries.Length ? offset + limit : null);
    }

    [McpServerTool(Name = "avamedia_save_word_library", Title = "保存自定义词库", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create/replace a custom library in the native desktop store, validating all candidates. Built-in IDs cannot be replaced. libraryId is a caller-owned stable ID. Does not rewrite existing task snapshots or run inference. Use read_word_library to retrieve candidates for new tasks.")]
    public McpWordLibraryInfo SaveWordLibrary(string libraryId, string name, WordCandidate[] entries, string source = "MCP")
    {
        if (string.IsNullOrWhiteSpace(libraryId) || libraryId.Length > 128 || libraryId.Any(char.IsControl)) throw new ArgumentException("词库标识无效。");
        if (entries.Any(entry => !VisibleWord(entry))) throw new ArgumentException("词库包含当前隐私设置不可用的标签。");
        new WordLibraryStore().Save(new(libraryId, name, source, entries));
        return new(libraryId, name, source, false, entries.Length);
    }

    [McpServerTool(Name = "avamedia_delete_word_library", Title = "删除自定义词库", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete one custom library and its desktop selection references through the native store. Built-in libraries cannot be deleted. Already absent custom IDs are harmless; existing task candidate snapshots remain intact.")]
    public bool DeleteWordLibrary(string libraryId)
    {
        if (WordLibraryCatalog.BuiltIns.Any(library => library.Id == libraryId)) throw new ArgumentException("内置词库不能删除。");
        var store = new WordLibraryStore(); store.Delete(libraryId); return !store.Libraries().Any(library => library.Id == libraryId);
    }
}
