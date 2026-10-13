using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AvaMedia.FileUi;

public sealed record FileBrowserItem(string Name, string Address, bool IsFolder, object? NativeItem = null);

public sealed record FileBrowserLocation(string Name, FileBrowserItem Folder, bool Device = false);
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
    Task<FileBrowserHistory> GetHistoryAsync(CancellationToken token) => Task.FromResult(new FileBrowserHistory([], [], []));
    Task<FileBrowserDirectory> ReadRecentAsync(bool folders, CancellationToken token)
        => Task.FromResult(new FileBrowserDirectory(new(folders ? "最近文件夹" : "最近文件", folders ? "avamedia:recent-folders" : "avamedia:recent", true), null, []));
    Task SetFavoriteAsync(FileBrowserItem folder, bool favorite, CancellationToken token) => Task.CompletedTask;
    Task RecordSelectionAsync(IReadOnlyList<FileBrowserItem> items, CancellationToken token) => Task.CompletedTask;
    Task<bool> GetGridModeAsync(CancellationToken token) => Task.FromResult(true);
    Task SetGridModeAsync(bool grid, CancellationToken token) => Task.CompletedTask;
    Task<FileBrowserMedia?> GetMediaAsync(FileBrowserEntry entry, CancellationToken token) => Task.FromResult<FileBrowserMedia?>(null);
}

public sealed record FileBrowserOptions(string Title, bool Folders = false, bool Multiple = false,
    IReadOnlyList<FilePickerFileType>? FileTypes = null, FileBrowserItem? StartFolder = null,
    FilePickerFileType? SelectedFileType = null, bool AllowFolders = false, bool Embedded = false, bool Compact = false);

/// <summary>Host-supplied glyphs for the compact presentation (FileUi ships no icon set).</summary>
public interface IFileBrowserGlyphs
{
    Control Glyph(string name);
    Control Glyph(string name, bool selected) => Glyph(name);
    Control Entry(FileBrowserItem item);
    Control Place(FileBrowserLocation location);
}

public sealed record FileBrowserVisit(FileBrowserItem Item, DateTimeOffset Time);
public sealed record FileBrowserHistory(IReadOnlyList<FileBrowserVisit> Files, IReadOnlyList<FileBrowserVisit> Folders,
    IReadOnlyList<FileBrowserItem> Favorites);
public sealed record FileBrowserMedia(double Duration, int Width, int Height, double FrameRate,
    string VideoCodec, string AudioCodec, byte[]? Thumbnail);

public enum FileBrowserKind { All, Video, Image, Audio, Subtitle, Pdf, Other }
public static class FileBrowserKinds
{
    // Classification is purely by extension and never starts a media probe.
    public static FileBrowserKind FromName(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v" or ".mpeg" or ".mpg" or ".mts" or ".m2ts" or ".ts" or ".wmv" or ".flv" or ".vob" or ".3gp" or ".mxf" => FileBrowserKind.Video,
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".avif" or ".svg" or ".ico" or ".raw" or ".dng" => FileBrowserKind.Image,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".aiff" or ".aif" or ".wma" or ".ac3" => FileBrowserKind.Audio,
        ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub" or ".sbv" => FileBrowserKind.Subtitle,
        ".pdf" => FileBrowserKind.Pdf,
        _ => FileBrowserKind.Other
    };
    public static string Name(FileBrowserKind kind) => kind switch
    {
        FileBrowserKind.Video => "视频", FileBrowserKind.Image => "图片", FileBrowserKind.Audio => "音频",
        FileBrowserKind.Subtitle => "字幕", FileBrowserKind.Pdf => "PDF", FileBrowserKind.Other => "文件", _ => "全部"
    };
    public static string Glyph(FileBrowserItem item) => item.IsFolder ? "folder" : FromName(item.Name) switch
    {
        FileBrowserKind.Video => "video", FileBrowserKind.Image => "image", FileBrowserKind.Audio => "music",
        FileBrowserKind.Subtitle => "captions", _ => "file-text"
    };
}
