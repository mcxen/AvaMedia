using Avalonia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;
using AvaMedia.FileUi;

internal static partial class Program
{
    // --picker captures only the requested three picker presentations, with real sample files.
    private static async Task CapturePicker()
    {
        Skin.Apply(Themes[_reviewTheme]);
        var state = new FilePickerStateStore(Path.Combine(_output, "picker-state"));
        var cache = FilePickerMediaCache.Create(new MediaEngine(new AppSettings()), new MediaThumbnailCache(maxConcurrency: 2));
        var window = new Window { Title = "选择媒体文件", Width = 1280, Height = 800, MinWidth = 760, MinHeight = 480 };
        var source = new DesktopFileBrowserSource(window.StorageProvider, state, cache);
        var folder = await source.ResolveAsync(_samples, Lifetime.Token) ?? throw new DirectoryNotFoundException(_samples);
        var directory = await source.ReadAsync(folder, Lifetime.Token);
        var videos = directory.Entries.Where(entry => FileBrowserKinds.FromName(entry.Name) == FileBrowserKind.Video).OrderBy(entry => entry.Name).ToArray();
        if (videos.Length == 0) throw new InvalidOperationException("Picker capture needs real video samples.");
        await state.RecordAsync(directory.Entries.Where(entry => !entry.IsFolder).Select(entry => entry.Item.Address), [_samples]);
        await state.FavoriteAsync(_samples, true);
        await state.SetGridAsync(true);
        var view = new FileBrowserView(source, new("选择媒体文件", Multiple: true, StartFolder: folder), Localization.Text, new DesktopFileBrowserGlyphs());
        window.Content = WindowChrome.WrapDialog(window, view, view.ExtractNavigationBar());
        WindowArtwork.SetKind(window, "document"); Skin.SetIsEnabled(window, true);
        window.Show();
        try
        {
            await view.InitializeAsync(); await view.SetMediaKindAsync(FileBrowserKind.Video);
            view.SelectAddresses(videos.Take(4).Select(entry => entry.Item.Address));
            // Settle visible covers before capturing; this does not change the production enumeration path.
            await Task.WhenAll(videos.Take(12).Select(entry => cache.GetAsync(entry.Item.Address, entry.Size, entry.Modified, Lifetime.Token)));
            await Capture(window, "impl-grid");
            await view.SetGridModeAsync(false); await Capture(window, "impl-list");
            await view.ShowRecentAsync(); await view.SetMediaKindAsync(FileBrowserKind.All);
            view.SelectAddresses(videos.Take(1).Select(entry => entry.Item.Address));
            await Capture(window, "impl-recent");
        }
        finally { view.Dispose(); window.Close(); }
    }
}
