using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AvaMedia.Desktop;

public partial class PlayerWindow
{
    private bool _capturing;
    private bool CanCaptureFrame => !_closed && !_deleting && !_capturing && !_pendingSeek
        && _info?.HasVideo == true && VideoImage.Source is Bitmap && (!Panorama.IsImmersive || PanoramaImage.HasFrame) && !string.IsNullOrEmpty(CurrentPath);

    private void RefreshCapture() => PlayerCaptureButton.IsEnabled = CanCaptureFrame;

    public async Task<string?> CaptureFrameAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanCaptureFrame) return null;
        var frame = (Bitmap)VideoImage.Source!;
        var source = CurrentPath;
        var position = _position;
        var revision = _revision;
        _capturing = true; RefreshCapture();
        try
        {
            using var png = new MemoryStream();
            // Freeze the displayed frame before yielding: playback can update or dispose it afterwards.
            if (Panorama.IsImmersive) PanoramaImage.SaveView(png, RenderScaling);
            else frame.Save(png);
            png.Position = 0;
            var output = await Task.Run(() => SaveCapturedFrame(png, source, position));
            if (Current(revision)) Notice(Localization.Format($"已保存截图：{Path.GetFileName(output)}\n{Path.GetDirectoryName(output)}"));
            return output;
        }
        catch (Exception ex)
        {
            if (Current(revision)) Notice(Localization.Format($"截图保存失败：{ex.Message}"));
            return null;
        }
        finally { _capturing = false; if (!_closed) RefreshCapture(); }
    }

    private static string SaveCapturedFrame(Stream png, string source, double position)
    {
        var folder = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source) + "_截图_" + EditorTime.Format(position).Replace(':', '-');
        for (var suffix = 0; ; suffix++)
        {
            var output = Path.Combine(folder, name + (suffix == 0 ? "" : $" ({suffix})") + ".png");
            FileStream file;
            try { file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(output) || Directory.Exists(output)) { continue; }
            try
            {
                using (file) png.CopyTo(file);
                return output;
            }
            catch
            {
                try { File.Delete(output); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }
}
