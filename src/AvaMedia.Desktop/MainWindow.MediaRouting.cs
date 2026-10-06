using Avalonia.Input;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private MediaRouteWindow? _mediaRouteWindow;
    public async Task RouteFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(VideoFolderScanner.PathComparer).ToArray();
        if (files.Length == 0) return;
        if (_mediaRouteWindow is { } existing) { existing.AddFiles(files); existing.Activate(); return; }
        var window = new MediaRouteWindow(Engine, files);
        _mediaRouteWindow = window;
        MediaRouteRequest? request;
        try { request = await window.ShowDialog<MediaRouteRequest?>(this); }
        finally { _mediaRouteWindow = null; }
        if (request is not null) await Configure(Catalog.Find(request.FeatureId), request.Files);
    }
    private void DragOver(object? sender, DragEventArgs e)
    { e.DragEffects = e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try { await RouteFilesAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); }
        catch (Exception error) { await Ui.Message(this, "打开工具失败", error.Message); }
    }
}
