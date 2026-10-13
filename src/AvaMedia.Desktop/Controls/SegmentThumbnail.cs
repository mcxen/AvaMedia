using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

internal interface ISegmentThumbnailSource
{
    Task<byte[]> ReadSegmentThumbnail(string path, ConversionOptions options, CancellationToken ct);
}

/// <summary>Only realized list rows decode thumbnails; detached rows release their image and request.</summary>
public sealed class SegmentThumbnail : Image
{
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);
    private readonly MediaFrameView _view;
    private ClipSegmentEntry? _entry;
    private ISegmentThumbnailSource? _owner;
    private readonly PreviewRequest _request = new();
    private (double Start, int Stream)? _loaded;
    /// <summary>Show the segment's last frame instead of its first.</summary>
    public bool AtEnd { get; set; }
    public SegmentThumbnail()
    {
        _view = new(this); DataContextChanged += (_, _) => Bind();
        DoubleTapped += (_, args) =>
        {
            if (Source is null || TopLevel.GetTopLevel(this) is not Window owner) return;
            args.Handled = true;
            ImageViewerWindow.ShowPreview(owner, Source, _entry?.Title ?? "");
        };
    }
    internal static async Task<byte[]> ReadAsync(IMediaPreview preview, string path, ConversionOptions options,
        CancellationToken token, CancellationToken lifetime, Task? ready = null)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        if (ready is not null) await ready.WaitAsync(request.Token);
        await DecodeGate.WaitAsync(request.Token);
        try { return await preview.Thumbnail(path, options.Start, VideoPreviewSizes.Row.Width, VideoPreviewSizes.Row.Height, request.Token, pad: false, videoStreamIndex: options.VideoStreamIndex); }
        finally { DecodeGate.Release(); }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.GetVisualAncestors().OfType<ISegmentThumbnailSource>().FirstOrDefault(); Bind();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unbind(); _owner = null; base.OnDetachedFromVisualTree(e);
    }
    private void Unbind()
    {
        if (_entry is not null) _entry.PropertyChanged -= EntryChanged;
        _entry = null; _loaded = null; _request.Cancel();
        _view.Clear();
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
        var options = entry.Options.Clone();
        if (AtEnd && entry.End > options.Start) options.Start = Math.Max(options.Start, entry.End - 0.05);
        var key = (options.Start, options.VideoStreamIndex);
        if (_loaded == key) return;
        _loaded = key; var token = _request.Restart(CancellationToken.None);
        _view.Clear();
        try
        {
            var data = await owner.ReadSegmentThumbnail(entry.SourcePath, options, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_entry, entry) || !_request.IsCurrent(token)) return;
            _view.Show(data);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_request.IsCurrent(token)) _loaded = null; }
    }
}
