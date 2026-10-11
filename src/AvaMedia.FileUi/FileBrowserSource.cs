using Avalonia.Platform.Storage;

namespace AvaMedia.FileUi;

public sealed record FileBrowserLocation(string Name, IStorageFolder Folder);
public sealed record FileBrowserEntry(IStorageItem Item, ulong? Size, DateTimeOffset? Modified, bool Hidden = false)
{
    public string Name => Item.Name;
    public bool IsFolder => Item is IStorageFolder;
}
public sealed record FileBrowserDirectory(IStorageFolder Folder, IStorageFolder? Parent, IReadOnlyList<FileBrowserEntry> Entries);

// Hosts supply local folders, remote server metadata, or user-granted browser files.
// Controls keep storage objects intact and never require a local filesystem path.
public interface IFileBrowserSource
{
    Task<IReadOnlyList<FileBrowserLocation>> GetLocationsAsync(CancellationToken cancellationToken);
    Task<IStorageItem?> ResolveAsync(string address, CancellationToken cancellationToken);
    Task<FileBrowserDirectory> ReadAsync(IStorageFolder folder, CancellationToken cancellationToken);
    string DisplayAddress(IStorageItem item);
}

public sealed record FileBrowserOptions(string Title, bool Folders = false, bool Multiple = false,
    IReadOnlyList<FilePickerFileType>? FileTypes = null, IStorageFolder? StartFolder = null,
    FilePickerFileType? SelectedFileType = null);
