using AvaMedia.Core;

namespace AvaMedia.Mcp;

/// <summary>Shared local file expansion for MCP tools. Access is unrestricted.</summary>
public sealed class McpFiles
{
    public string Check(string path, bool mustExist = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请提供绝对文件路径。");
        var full = Path.GetFullPath(path);
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("文件或目录不存在。", full);
        return full;
    }

    public string[] Collect(string[] paths, bool recursive, bool mediaOnly, CancellationToken ct)
    {
        if (paths.Length == 0) throw new ArgumentException("请提供文件或目录路径。");
        var seen = new HashSet<string>(BatchRename.PathComparer);
        var files = new List<string>();
        void Add(string path)
        {
            ct.ThrowIfCancellationRequested(); var full = Check(path, true);
            if ((!mediaOnly || MediaTagService.Supports(full)) && seen.Add(full)) files.Add(full);
        }
        foreach (var path in paths)
        {
            var full = Check(path, true);
            if (File.Exists(full)) Add(full);
            else foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions
            { RecurseSubdirectories = recursive, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }).OrderBy(file => file, BatchRename.PathComparer)) Add(file);
        }
        if (files.Count == 0) throw new ArgumentException("没有可处理的文件。");
        return files.ToArray();
    }
}
