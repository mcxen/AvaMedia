using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class ImageViewerWindow
{
    private Task ExportAsync()
    {
        if (_entries.Length == 0) return Task.CompletedTask;
        var window = new ImageViewerExportWindow(_entries, Math.Max(0, _index), _viewport.Rotation, _viewport.Flipped);
        return window.ShowDialog(this);
    }
    private async Task TransferAsync(bool move)
    {
        if (_index < 0 || _index >= _entries.Length) return;
        var entry = _entries[_index];
        if (move && entry.InArchive) throw new InvalidOperationException("压缩包中的图片可复制到文件夹，移动请先解压。");
        var folder = await Ui.Folder(this, move ? "将当前图片移动到" : "将当前图片复制到");
        if (folder is null) return;
        var name = entry.Name.Replace('\\', '/').Split('/')[^1];
        var extension = Path.GetExtension(name).TrimStart('.'); var stem = Path.GetFileNameWithoutExtension(name);
        var destination = MediaEngine.UniqueOutput(folder, stem, extension);
        if (move)
        {
            // File.Move leaves the source untouched if the destination cannot be written.
            await Task.Run(() => File.Move(entry.Container, destination, overwrite: false), _lifetime.Token);
            _preferences.Bookmarks = _preferences.Bookmarks.Select(item => item.Identity == entry.Identity
                ? new ImageViewerEntry(destination, Bytes: entry.Bytes) : item).ToArray(); SavePreferences();
            _entries = _entries.Where(item => item.Identity != entry.Identity).ToArray(); RenderFiles();
            if (_entries.Length > 0) await LoadAsync(Math.Min(_index, _entries.Length - 1));
            else { _index = -1; ReleaseImages(); _empty.Text = Localization.Text("没有可显示的图片"); _empty.IsVisible = true; _status.Text = ""; }
        }
        else
        {
            var bytes = await ImageViewerSource.ReadAsync(entry, _lifetime.Token);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            { await File.WriteAllBytesAsync(temporary, bytes, _lifetime.Token); File.Move(temporary, destination, overwrite: false); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _status.Text = Localization.Text("已复制") + " · " + destination;
        }
    }
}
