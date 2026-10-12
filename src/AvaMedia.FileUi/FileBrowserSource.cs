using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AvaMedia.FileUi;

public sealed record FileBrowserItem(string Name, string Address, bool IsFolder, object? NativeItem = null);

public sealed record FileBrowserLocation(string Name, FileBrowserItem Folder);
public sealed record FileBrowserEntry(FileBrowserItem Item, ulong? Size, DateTimeOffset? Modified, bool Hidden = false)
{
    public string Name => Item.Name;
    public bool IsFolder => Item.IsFolder;
}
public sealed record FileBrowserDirectory(FileBrowserItem Folder, FileBrowserItem? Parent, IReadOnlyList<FileBrowserEntry> Entries);

// Hosts supply local folders, remote server metadata, or user-granted browser files.
// Controls keep storage objects intact and never require a local filesystem path.
public interface IFileBrowserSource
{
    Task<IReadOnlyList<FileBrowserLocation>> GetLocationsAsync(CancellationToken cancellationToken);
    Task<FileBrowserItem?> ResolveAsync(string address, CancellationToken cancellationToken);
    Task<FileBrowserDirectory> ReadAsync(FileBrowserItem folder, CancellationToken cancellationToken);
    async Task<IReadOnlyList<FileBrowserItem>> ReadFoldersAsync(FileBrowserItem folder, CancellationToken cancellationToken)
        => (await ReadAsync(folder, cancellationToken)).Entries.Where(entry => entry.IsFolder && !entry.Hidden).Select(entry => entry.Item).ToArray();
    string DisplayAddress(FileBrowserItem item);
}

public sealed record FileBrowserOptions(string Title, bool Folders = false, bool Multiple = false,
    IReadOnlyList<FilePickerFileType>? FileTypes = null, FileBrowserItem? StartFolder = null,
    FilePickerFileType? SelectedFileType = null, bool AllowFolders = false, bool Embedded = false, bool Compact = false);

/// <summary>Host-supplied glyphs for the compact presentation (FileUi ships no icon set).</summary>
public interface IFileBrowserGlyphs
{
    Control Glyph(string name);
    Control Entry(FileBrowserItem item);
    Control Place(FileBrowserLocation location);
}
