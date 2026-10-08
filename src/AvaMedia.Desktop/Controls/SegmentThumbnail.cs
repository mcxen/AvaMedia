using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop.Controls;

/// <summary>Only realized list rows decode thumbnails; detached rows release their image and request.</summary>
public sealed class SegmentThumbnail : Image
{
    private ClipSegmentEntry? _entry;
    private EditorWindow? _owner;
    private CancellationTokenSource? _request;
    private (double Start, int Stream)? _loaded;
    public SegmentThumbnail() => DataContextChanged += (_, _) => Bind();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.GetVisualAncestors().OfType<EditorWindow>().FirstOrDefault(); Bind();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unbind(); _owner = null; base.OnDetachedFromVisualTree(e);
    }
    private void Unbind()
    {
        if (_entry is not null) _entry.PropertyChanged -= EntryChanged;
        _entry = null; _loaded = null; _request?.Cancel(); _request?.Dispose(); _request = null;
        var old = Source as Bitmap; Source = null; old?.Dispose();
    }
    private void Bind()
    {
        Unbind();
        if (_owner is null || DataContext is not ClipSegmentEntry entry) return;
        _entry = entry; entry.PropertyChanged += EntryChanged; Refresh();
    }
    private void EntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipSegmentEntry.Options)) Refresh();
    }
    private async void Refresh()
    {
        if (_entry is not { } entry || _owner is not { } owner) return;
        var key = (entry.Options.Start, entry.Options.VideoStreamIndex);
        if (_loaded == key) return;
        _loaded = key; _request?.Cancel(); _request?.Dispose();
        var request = _request = new CancellationTokenSource(); var token = request.Token;
        var old = Source as Bitmap; Source = null; old?.Dispose();
        try
        {
            var data = await owner.ReadSegmentThumbnail(entry.Options.Clone(), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_entry, entry) || !ReferenceEquals(_request, request)) return;
            using var stream = new MemoryStream(data); Source = new Bitmap(stream);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (ReferenceEquals(_request, request)) _loaded = null; }
    }
}
