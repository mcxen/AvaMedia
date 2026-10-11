using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AvaMedia.FileUi;

namespace AvaMedia.Desktop;

/// <summary>All toolbox file dialogs enter here; media tools decide how selected files are processed.</summary>
internal static class ToolFilePicker
{
    private static IStorageFolder? _lastFolder;

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
        Func<Window, Task<IReadOnlyList<IStorageItem>>> external)
    {
        var view = new FileBrowserView(new DesktopFileBrowserSource(owner.StorageProvider), options, Localization.Text);
        Localization.SetIsUserText(view, true);
        var dialog = new Window
        {
            Title = Localization.Text(options.Title), Width = 880, Height = 600, MinWidth = 680, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = view
        };
        Controls.WindowArtwork.SetKind(dialog, "document");
        view.PickExternalAsync = async () => (await external(dialog)).Select(item => DesktopFileBrowserSource.Wrap(item)!).ToArray();
        view.Selected += selected => { _lastFolder = view.CurrentFolder?.NativeItem as IStorageFolder; dialog.Close(selected.Select(item => (IStorageItem)item.NativeItem!).ToArray()); };
        view.Cancelled += () => dialog.Close();
        dialog.Opened += async (_, _) => await view.InitializeAsync();
        dialog.Closed += (_, _) => view.Dispose();
        return await dialog.ShowDialog<IReadOnlyList<IStorageItem>?>(owner) ?? [];
    }
}

internal sealed class DesktopFileBrowserSource(IStorageProvider storage) : IFileBrowserSource
{
    public async Task<IReadOnlyList<FileBrowserLocation>> GetLocationsAsync(CancellationToken cancellationToken)
    {
        var locations = new List<FileBrowserLocation>();
        var known = new HashSet<Uri>();
        async Task Add(string label, string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (await storage.TryGetFolderFromPathAsync(path) is { } folder && known.Add(folder.Path))
                    locations.Add(new(Localization.Text(label), Wrap(folder)!));
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
        foreach (var drive in DriveInfo.GetDrives()) await Add(drive.Name, drive.Name);
        return locations;
    }

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
            await foreach (var item in ((IStorageFolder)folder.NativeItem!).GetItemsAsync().WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong? size = null; DateTimeOffset? modified = null;
                var hidden = item.Name.StartsWith('.');
                try
                {
                    var properties = await item.GetBasicPropertiesAsync(); size = properties.Size; modified = properties.DateModified;
                    if (item.TryGetLocalPath() is { } path) hidden |= (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                entries.Add(new(Wrap(item)!, size, modified, hidden));
            }
            return new FileBrowserDirectory(folder, Wrap(await ((IStorageFolder)folder.NativeItem!).GetParentAsync()), entries);
        }, cancellationToken);

    public string DisplayAddress(FileBrowserItem item) => item.Address;
    internal static FileBrowserItem? Wrap(IStorageItem? item) => item is null ? null : new(item.Name, item.TryGetLocalPath() ?? item.Path.ToString(), item is IStorageFolder, item);
}
