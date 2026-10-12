using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using static AvaMedia.Desktop.Classification.ClassifyUi;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Classification;

/// <summary>
/// Lazy cover: only requests a thumbnail while realized and inside the viewport. Thumbnails come from the shared
/// on-disk ffmpeg cache (downscaled decode off the UI thread); the small JPEG is decoded to width on a worker.
/// </summary>
internal sealed class ClassifyThumb : Border
{
    public static readonly MediaThumbnailCache Cache = new();
    private static readonly object Sync = new();
    private static readonly LinkedList<(string Key, Bitmap Bitmap)> Recent = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> Decoded = [];
    private const int MaxDecoded = 600;
    public static int Requests, Hits;

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private string? _path; private double? _seconds; private int _width;
    private CancellationTokenSource? _request;
    private bool _visible;

    public ClassifyThumb(int decodeWidth = 96)
    {
        _width = decodeWidth; ClipToBounds = true; Bind(BackgroundProperty, Resource(Fill)); CornerRadius = new(6); Child = _image;
        EffectiveViewportChanged += (_, args) =>
        {
            var visible = args.EffectiveViewport.Width > 0 && args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            if (visible == _visible) return;
            _visible = visible; if (visible) Load(); else Cancel();
        };
    }

    public void SetSource(string? path, double? seconds = null)
    {
        if (_path == path && _seconds == seconds) return;
        _path = path; _seconds = seconds; Cancel(); _image.Source = null;
        if (_visible) Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _visible = false; Cancel(); base.OnDetachedFromVisualTree(e); }

    private void Cancel() { _request?.Cancel(); _request = null; }

    private string Key => _path + "|" + _seconds + "|" + _width;

    private void Load()
    {
        if (_path is not { } path || _image.Source is not null) return;
        Interlocked.Increment(ref Requests);
        lock (Sync)
            if (Decoded.TryGetValue(Key, out var node))
            { Recent.Remove(node); Recent.AddFirst(node); _image.Source = node.Value.Bitmap; Interlocked.Increment(ref Hits); return; }
        var request = _request = new CancellationTokenSource();
        var key = Key; var width = _width; var seconds = _seconds;
        _ = Task.Run(async () =>
        {
            try
            {
                // Generate at 2x for HiDPI; decode-to-width keeps memory bounded.
                var file = await Cache.GetAsync(path, Math.Max(64, width * 2), seconds, request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                await using var stream = File.OpenRead(file);
                var bitmap = Bitmap.DecodeToWidth(stream, Math.Max(32, width * 2), BitmapInterpolationMode.MediumQuality);
                lock (Sync)
                {
                    if (!Decoded.ContainsKey(key))
                    {
                        Decoded[key] = Recent.AddFirst((key, bitmap));
                        while (Recent.Count > MaxDecoded) { var last = Recent.Last!; Recent.RemoveLast(); Decoded.Remove(last.Value.Key); }
                    }
                }
                Dispatcher.UIThread.Post(() => { if (!request.IsCancellationRequested && Key == key) _image.Source = bitmap; }, DispatcherPriority.Background);
            }
            catch (Exception) { }
        });
    }
}
