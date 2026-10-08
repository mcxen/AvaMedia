using Avalonia.Input;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private MediaRouteWindow? _mediaRouteWindow;
    public async Task RouteFilesAsync(IEnumerable<string> paths)
    {
        try { await RouteFilesCoreAsync(paths); }
        catch (Exception error)
        {
            AppDiagnostics.Record("Import dropped files", error);
            if (!_closing && IsVisible) await Ui.Message(this, "导入文件失败", error.Message);
        }
    }
    private async Task RouteFilesCoreAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(VideoFolderScanner.PathComparer).ToArray();
        if (files.Length == 0) return;
        if (_mediaRouteWindow is { } existing) { existing.AddFiles(files); existing.Activate(); return; }
        var window = new MediaRouteWindow(Engine, files, new MediaFileRouter(_settings.EnableBetaFeatures));
        _mediaRouteWindow = window;
        MediaRouteRequest? request;
        try { request = await window.ShowForRoutingAsync(this); }
        finally { _mediaRouteWindow = null; }
        if (request is not null) await Configure(Catalog.Find(request.FeatureId), request.Files);
    }
    private void DragOver(object? sender, DragEventArgs e)
    { if (e.Handled) return; e.DragEffects = e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Drop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        try { await RouteFilesAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); }
        catch (Exception error)
        {
            AppDiagnostics.Record("Read dropped files", error);
            if (!_closing && IsVisible) await Ui.Message(this, "导入文件失败", error.Message);
        }
    }
}
