using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop;

/// <summary>Only realized thumbnails load. Native bitmap decoding also runs away from the UI thread.</summary>
internal sealed class PersonTaskThumbnail : Image
{
    private CancellationTokenSource? _request;
    private ClipSegmentEntry? _entry;
    private PersonTaskView? _owner;
    private double? _start;
    public PersonTaskThumbnail() => DataContextChanged += (_, _) => BindEntry();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.GetVisualAncestors().OfType<PersonTaskView>().FirstOrDefault(); BindEntry();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Release(); _owner = null; base.OnDetachedFromVisualTree(e);
    }
    private void Release()
    {
        _request?.Cancel(); _request?.Dispose(); _request = null;
        if (_entry is not null) _entry.PropertyChanged -= Changed;
        _entry = null; _start = null;
        var old = Source as Bitmap; Source = null; old?.Dispose();
    }
    private void BindEntry()
    {
        Release();
        if (_owner is null || DataContext is not ClipSegmentEntry entry) return;
        _entry = entry; _entry.PropertyChanged += Changed; Load();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ClipSegmentEntry.Options)) Load(); }
    private async void Load()
    {
        if (_entry is not { } entry || _owner is not { } owner || _start == entry.Options.Start) return;
        _start = entry.Options.Start; var options = entry.Options.Clone();
        _request?.Cancel(); _request?.Dispose(); var work = _request = new(); var token = work.Token;
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                var bytes = await owner.ReadSegmentThumbnail(entry.SourcePath, options, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); using var stream = new MemoryStream(bytes); return new Bitmap(stream);
            }, token);
            if (token.IsCancellationRequested || _request != work) { bitmap.Dispose(); return; }
            var old = Source as Bitmap; Source = bitmap; old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_request == work) { var old = Source as Bitmap; Source = null; old?.Dispose(); } }
    }
}
