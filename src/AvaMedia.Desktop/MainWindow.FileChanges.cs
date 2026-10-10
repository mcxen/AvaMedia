using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void SynchronizeFileChanges(IReadOnlyList<RenameItem> mappings)
    {
        _queue.ApplyFileChanges(_jobs, mappings);
        Save(); Refresh();
    }

    private async Task QueueFilesChangedAsync(IReadOnlyList<RenameItem> mappings) =>
        await Dispatcher.UIThread.InvokeAsync(() => SynchronizeFileChanges(mappings));

    private ImageViewerWindow CreateImageViewerWindow(IEnumerable<string>? files)
    {
        var window = new ImageViewerWindow(files, reserveFiles: _queue.ReserveFiles);
        window.Moved += SynchronizeFileChanges;
        return window;
    }
}
