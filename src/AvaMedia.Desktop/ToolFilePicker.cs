using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AvaMedia.FileUi;
using AvaMedia.Core;
using Avalonia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>All toolbox file dialogs enter here; media tools decide how selected files are processed.</summary>
internal static class ToolFilePicker
{
    private static IStorageFolder? _lastFolder;

    internal static async Task<string[]> OpenRecentFilesAsync(Window owner)
    {
        var options = new FilePickerOpenOptions { Title = "最近使用", AllowMultiple = true };
        var selected = await ShowAsync(owner, new(options.Title, Multiple: true),
            async dialog => (await dialog.StorageProvider.OpenFilePickerAsync(options)).Cast<IStorageItem>().ToArray(), startRecent: true);
        return selected.OfType<IStorageFile>().Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
    }

    public static async Task<IReadOnlyList<IStorageFile>> OpenFilesAsync(Window owner, FilePickerOpenOptions options)
    {
        var selected = await ShowAsync(owner, new(options.Title ?? "选择文件", Multiple: options.AllowMultiple,
            FileTypes: options.FileTypeFilter, StartFolder: DesktopFileBrowserSource.Wrap(options.SuggestedStartLocation ?? _lastFolder),
            SelectedFileType: options.SuggestedFileType),
            async dialog => (await dialog.StorageProvider.OpenFilePickerAsync(options)).Cast<IStorageItem>().ToArray());
        return selected.OfType<IStorageFile>().ToArray();
    }

    public static async Task<IReadOnlyList<IStorageFolder>> OpenFoldersAsync(Window owner, FolderPickerOpenOptions options)
    {
        var selected = await ShowAsync(owner, new(options.Title ?? "选择文件夹", Folders: true, Multiple: options.AllowMultiple,
            StartFolder: DesktopFileBrowserSource.Wrap(options.SuggestedStartLocation ?? _lastFolder)),
            async dialog => (await dialog.StorageProvider.OpenFolderPickerAsync(options)).Cast<IStorageItem>().ToArray());
        return selected.OfType<IStorageFolder>().ToArray();
    }

    // Saving retains the platform's filename, overwrite and write-permission protocol.
    public static Task<IStorageFile?> SaveAsync(Window owner, FilePickerSaveOptions options)
        => owner.StorageProvider.SaveFilePickerAsync(options);

    public static FileImportActions ImportActions(Window owner, Func<Task> files, Func<Task>? folders = null, string filesText = "添加文件…")
        => new()
        {
            FilesText = filesText, AllowFolders = folders is not null, AddFilesAsync = files, AddFoldersAsync = folders,
            ReportErrorAsync = error => Ui.Message(owner, "导入失败", error.Message)
        };

    private static async Task<IReadOnlyList<IStorageItem>> ShowAsync(Window owner, FileBrowserOptions options,
        Func<Window, Task<IReadOnlyList<IStorageItem>>> external, bool startRecent = false)
    {
        var source = new DesktopFileBrowserSource(owner.StorageProvider);
        var view = new FileBrowserView(source, options, Localization.Text, new DesktopFileBrowserGlyphs(), Localization.Format);
        var dialog = CreateDialog(view, options, startRecent);
        view.PickExternalAsync = async () => (await external(dialog)).Select(item => DesktopFileBrowserSource.Wrap(item)!).ToArray();
        var completion = new TaskCompletionSource<IReadOnlyList<IStorageItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => completion.TrySetResult([]);
        var selecting = false;
        view.Selected += async selected =>
        {
            if (selecting) return;
            selecting = true; view.IsEnabled = false;
            try
            {
                var items = new List<IStorageItem>();
                foreach (var item in selected)
                {
                    var resolved = item.NativeItem as IStorageItem ?? (await source.ResolveAsync(item.Address, CancellationToken.None))?.NativeItem as IStorageItem;
                    if (resolved is null) throw new IOException(Localization.Text("找不到可选择的文件或文件夹。"));
                    items.Add(resolved);
                }
                await source.RecordSelectionAsync(selected, CancellationToken.None);
                _lastFolder = view.CurrentFolder?.NativeItem as IStorageFolder ?? await items[0].GetParentAsync();
                completion.TrySetResult(items.ToArray());
                dialog.Close();
            }
            catch (Exception error) { await Ui.Message(dialog, "打开失败", error.Message); }
            finally { selecting = false; view.IsEnabled = true; }
        };
        // A file picker must not disable the queue window while media is being chosen.
        // Retain owner lifetime, but show an ordinary independent window rather than a modal sheet.
        void OwnerClosed(object? sender, EventArgs args) => dialog.Close();
        owner.Closed += OwnerClosed;
        try { dialog.Show(owner); return await completion.Task; }
        finally { owner.Closed -= OwnerClosed; }
    }

    internal static Window CreateDialog(FileBrowserView view, FileBrowserOptions options, bool startRecent = false)
    {
        Localization.SetIsUserText(view, true);
        var dialog = new Window
        {
            Title = Localization.Text(options.Title), Width = 1280, Height = 800, MinWidth = 760, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        // Build the complete logical tree before attaching it to the window. Attaching view first
        // would leave it in both parents' child collections; replacing Content then clears its parent.
        dialog.Content = WindowChrome.WrapDialog(dialog, view, view.ExtractNavigationBar());
        Skin.SetIsEnabled(dialog, true);
        Controls.WindowArtwork.SetKind(dialog, "document");
        view.Cancelled += () => dialog.Close();
        dialog.Opened += async (_, _) =>
        {
            await view.InitializeAsync();
            if (startRecent) await view.ShowRecentAsync();
        };
        dialog.Closed += (_, _) => view.Dispose();
        return dialog;
    }
}

internal sealed class DesktopFileBrowserSource(IStorageProvider storage, FilePickerStateStore? state = null, FilePickerMediaCache? media = null) : IFileBrowserSource
{
    private static readonly FilePickerStateStore SharedState = new();
    private static readonly Lazy<FilePickerMediaCache> SharedMedia = new(() => FilePickerMediaCache.Create(new MediaEngine(new Storage().LoadSettings()), new MediaThumbnailCache(maxConcurrency: 2)));
    private readonly FilePickerStateStore _state = state ?? SharedState;
    private FilePickerMediaCache Media => media ?? SharedMedia.Value;
    public Task<IReadOnlyList<FileBrowserLocation>> GetLocationsAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<FileBrowserLocation>>(async () =>
    {
        var locations = new List<FileBrowserLocation>();
        var known = new HashSet<Uri>();
        async Task Add(string label, string path, bool device = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (await storage.TryGetFolderFromPathAsync(path) is { } folder && known.Add(folder.Path))
                    locations.Add(new(Localization.Text(label), Wrap(folder)!, device));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        await Add("个人文件夹", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        await Add("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads) is { } downloads && known.Add(downloads.Path))
            locations.Add(new(Localization.Text("下载"), Wrap(downloads)!));
        await Add("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        await Add("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        await Add("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!OperatingSystem.IsWindows() && drive.Name != "/" && !new[] { "/Volumes/", "/media/", "/mnt/", "/home/", "/workspace" }
                .Any(prefix => drive.Name.StartsWith(prefix, StringComparison.Ordinal))) continue;
            if (!OperatingSystem.IsWindows() && new[] { "/proc", "/sys", "/dev", "/run", "/snap", "/boot" }
                .Any(prefix => drive.Name.TrimEnd('/') == prefix || drive.Name.StartsWith(prefix + "/", StringComparison.Ordinal))) continue;
            try { if (drive.IsReady) await Add(drive.Name == Path.GetPathRoot(Environment.CurrentDirectory) ? "系统磁盘" : string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel, drive.Name, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return locations;
    }, cancellationToken);

    public async Task<FileBrowserItem?> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (address == "~") address = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (address.StartsWith("~/", StringComparison.Ordinal)) address = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), address[2..]);
        if (!Path.IsPathFullyQualified(address)) return null;
        return Wrap((IStorageItem?)await storage.TryGetFolderFromPathAsync(address) ?? await storage.TryGetFileFromPathAsync(address));
    }

    public Task<FileBrowserDirectory> ReadAsync(FileBrowserItem folder, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            var entries = new List<FileBrowserEntry>();
            if (folder.Address is "avamedia:recent" or "avamedia:recent-folders")
            {
                var history = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var visit in folder.Address == "avamedia:recent" ? history.Files : history.Folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { entries.Add(LocalEntry(folder.Address == "avamedia:recent" ? new FileInfo(visit.Path) : new DirectoryInfo(visit.Path))); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                return new FileBrowserDirectory(folder, null, entries);
            }
            if (Path.IsPathFullyQualified(folder.Address))
            {
                var directory = new DirectoryInfo(folder.Address);
                foreach (var item in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { entries.Add(LocalEntry(item)); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                var parent = directory.Parent;
                return new FileBrowserDirectory(folder, parent is null ? null : new(parent.Name, parent.FullName, true), entries);
            }
            // Non-local storage providers retain their granted storage objects.
            var native = folder.NativeItem as IStorageFolder ?? (await ResolveAsync(folder.Address, cancellationToken))?.NativeItem as IStorageFolder
                ?? throw new DirectoryNotFoundException(folder.Address);
            await foreach (var item in native.GetItemsAsync().WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong? size = null; DateTimeOffset? modified = null;
                try { var properties = await item.GetBasicPropertiesAsync(); size = properties.Size; modified = properties.DateModified; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                entries.Add(new(Wrap(item)!, size, modified, item.Name.StartsWith('.')));
            }
            return new FileBrowserDirectory(folder, Wrap(await native.GetParentAsync()), entries);
        }, cancellationToken);

    private static FileBrowserEntry LocalEntry(FileSystemInfo info)
        => new(new(info.Name, info.FullName, info is DirectoryInfo), info is FileInfo file ? (ulong)file.Length : null,
            new DateTimeOffset(info.LastWriteTimeUtc), info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden));

    public async Task<IReadOnlyList<FileBrowserItem>> ReadFoldersAsync(FileBrowserItem folder, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(folder.Address)) return (await ReadAsync(folder, token)).Entries.Where(entry => entry.IsFolder && !entry.Hidden).Select(entry => entry.Item).ToArray();
        return await Task.Run(() => new DirectoryInfo(folder.Address).EnumerateDirectories().Where(info =>
        { token.ThrowIfCancellationRequested(); return !info.Name.StartsWith('.') && !info.Attributes.HasFlag(FileAttributes.Hidden); })
            .Select(info => new FileBrowserItem(info.Name, info.FullName, true)).ToArray(), token);
    }
    public Task<FileBrowserDirectory> ReadRecentAsync(bool folders, CancellationToken token)
        => ReadAsync(new(folders ? "最近文件夹" : "最近文件", folders ? "avamedia:recent-folders" : "avamedia:recent", true), token);

    public async Task<FileBrowserHistory> GetHistoryAsync(CancellationToken token)
    {
        var history = await _state.LoadAsync(token);
        FileBrowserItem Item(string path, bool folder) => new(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path, path, folder);
        return new(history.Files.Select(visit => new FileBrowserVisit(Item(visit.Path, false), visit.Time)).ToArray(),
            history.Folders.Select(visit => new FileBrowserVisit(Item(visit.Path, true), visit.Time)).ToArray(), history.Favorites.Select(path => Item(path, true)).ToArray());
    }
    public async Task RecordSelectionAsync(IReadOnlyList<FileBrowserItem> items, CancellationToken token)
        => await _state.RecordAsync(items.Where(item => !item.IsFolder && Path.IsPathFullyQualified(item.Address)).Select(item => item.Address),
            items.Where(item => item.IsFolder && Path.IsPathFullyQualified(item.Address)).Select(item => item.Address), token);
    public async Task SetFavoriteAsync(FileBrowserItem folder, bool favorite, CancellationToken token) => await _state.FavoriteAsync(folder.Address, favorite, token);
    public async Task<bool> GetGridModeAsync(CancellationToken token) => (await _state.LoadAsync(token)).Grid;
    public async Task SetGridModeAsync(bool grid, CancellationToken token) => await _state.SetGridAsync(grid, token);
    public async Task<FileBrowserMedia?> GetMediaAsync(FileBrowserEntry entry, CancellationToken token)
    {
        if (entry.IsFolder || !Path.IsPathFullyQualified(entry.Item.Address)) return null;
        var result = await Media.GetAsync(entry.Item.Address, entry.Size, entry.Modified, token).ConfigureAwait(false);
        return new(result.Duration, result.Width, result.Height, result.FrameRate, result.VideoCodec, result.AudioCodec, result.Thumbnail);
    }

    public string DisplayAddress(FileBrowserItem item) => item.Address;
    internal static FileBrowserItem? Wrap(IStorageItem? item) => item is null ? null : new(item.Name, item.TryGetLocalPath() is { } local ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(local)) : item.Path.ToString(), item is IStorageFolder, item);
}

internal sealed class DesktopFileBrowserGlyphs : IFileBrowserGlyphs
{
    public Control Glyph(string name) => Glyph(name, false);
    public Control Glyph(string name, bool selected)
    {
        var icon = new LucideIcon { Kind = name.Replace("-muted", "").Replace("-white", ""), Width = 16, Height = 16 };
        icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension(name.EndsWith("-white", StringComparison.Ordinal) ? "FbCheckText" : selected ? "FbSelectedGlyph" : name.EndsWith("-muted", StringComparison.Ordinal) ? "FbMuted" : "FbAccent"));
        return icon;
    }
    public Control Entry(FileBrowserItem item) => Glyph(FileBrowserKinds.Glyph(item));
    public Control Place(FileBrowserLocation location) => Glyph(location.Device ? "hard-drive" : "folder");
}
