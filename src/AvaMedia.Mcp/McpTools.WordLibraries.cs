using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpWordLibraryInfo(string Id, string Name, string Source, bool BuiltIn, int Entries);
public sealed record McpWordPage(WordCandidate[] Entries, int Total, int Offset, int? NextOffset);

public sealed partial class McpTools
{
    private bool VisibleWord(WordCandidate entry) => PrivateEnabled || !MediaPrivacy.IsSensitiveLabel(entry.Label, entry.Category, entry.Tags);

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
