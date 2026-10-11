using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpFileLocation(string Name, string Path);
public sealed record McpFileLocations(string DefaultFolder, McpFileLocation[] Locations);
public sealed record McpDirectoryEntry(string Path, string Name, bool IsDirectory, long? Size,
    DateTime? ModifiedUtc, bool Hidden);
public sealed record McpDirectoryPage(string Path, string? Parent, McpDirectoryEntry[] Entries,
    int Total, int Offset, int? NextOffset);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_file_locations", Title = "服务器文件位置", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Discover existing output/home/media folders and drives on the AvaMedia server for a remote file picker. All paths belong to this server, not the browser or MCP client. Does not upload, download or create files.")]
    public McpFileLocations FileLocations(CancellationToken ct = default)
    {
        var locations = new List<McpFileLocation>();
        var known = new HashSet<string>(AvaMedia.Core.BatchRename.PathComparer);
        void Add(string name, string path)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return;
            path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            if (known.Add(path)) locations.Add(new(name, path));
        }
        Add("输出目录", workspace.Engine.Settings.OutputFolder);
        Add("个人文件夹", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add("下载", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        Add("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        foreach (var drive in DriveInfo.GetDrives()) Add(drive.Name, drive.Name);
        return new(locations.FirstOrDefault()?.Path ?? "", locations.ToArray());
    }

    [McpServerTool(Name = "avamedia_browse_directory", Title = "浏览服务器目录", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Browse one directory on the AvaMedia server, returning folders and files with absolute server paths, parent directory, sizes and UTC modification times. Entries are sorted folders first, then by name. search filters names in this directory only; extensions filters files but keeps directories navigable. Preserve returned paths unchanged and bind selections to the connected server. Submit selected paths with avamedia_create_task; media stays on the server. Offset pagination is a fresh view, not a filesystem snapshot.")]
    public McpDirectoryPage BrowseDirectory(string folder, string? search = null, string[]? extensions = null,
        bool foldersOnly = false, bool showHidden = false, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        Page(offset, limit);
        var path = System.IO.Path.TrimEndingDirectorySeparator(_files.Check(folder, true));
        if (!Directory.Exists(path)) throw new ArgumentException("请选择服务器上的文件夹。");
        var filter = extensions?.Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim().TrimStart('.'))
            .Where(extension => extension.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = search?.Trim() ?? "";
        var entries = new List<McpDirectoryEntry>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
        { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }))
        {
            ct.ThrowIfCancellationRequested();
            var isDirectory = item is DirectoryInfo;
            if (foldersOnly && !isDirectory || !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (!isDirectory && filter is { Count: > 0 } && !filter.Contains(item.Extension.TrimStart('.'))) continue;
            long? size = null; DateTime? modified = null;
            var hidden = item.Name.StartsWith('.');
            try
            {
                hidden |= (item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                modified = item.LastWriteTimeUtc;
                if (item is FileInfo file) size = file.Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!showHidden && hidden) continue;
            entries.Add(new(item.FullName, item.Name, isDirectory, size, modified, hidden));
        }
        ct.ThrowIfCancellationRequested();
        var ordered = entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Name, StringComparer.Ordinal);
        return new(path, Directory.GetParent(path)?.FullName, ordered.Skip(offset).Take(limit).ToArray(),
            entries.Count, offset, (long)offset + limit < entries.Count ? offset + limit : null);
    }
}
