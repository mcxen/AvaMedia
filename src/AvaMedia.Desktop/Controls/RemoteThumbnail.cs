using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop.Controls;

public sealed class RemoteThumbnail : UserControl
{
    public static readonly StyledProperty<string?> SourceUrlProperty = AvaloniaProperty.Register<RemoteThumbnail,string?>(nameof(SourceUrl));
    public string? SourceUrl { get => GetValue(SourceUrlProperty); set => SetValue(SourceUrlProperty,value); }
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly FeatureIcon _fallback = new() { Kind = "video", Width = 36, Height = 36 };
    private CancellationTokenSource? _work;
    private Bitmap? _bitmap;
    private bool _attached;
    public RemoteThumbnail()
    {
        var root = new Grid(); root.Children.Add(_fallback); root.Children.Add(_image); Content = root;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceUrlProperty) return;
        CancelLoad(); ClearBitmap();
        if (_attached) _ = LoadAsync();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; if (_bitmap is null && _work is null) _ = LoadAsync(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; CancelLoad(); ClearBitmap(); base.OnDetachedFromVisualTree(e); }

    private void CancelLoad() { var work = _work; _work = null; work?.Cancel(); }
    private void ClearBitmap()
    { _image.Source = null; _bitmap?.Dispose(); _bitmap = null; _fallback.IsVisible = true; }

    private async Task LoadAsync()
    {
        if (!_attached || _work is not null || !Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")) return;
        var operation = new CancellationTokenSource(); _work = operation; var token = operation.Token;
        try
        {
            using var response=await Client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is > 5242880)return;
            await using var input=await response.Content.ReadAsStreamAsync(token);using var data=new MemoryStream();var buffer=new byte[16384];
            while(await input.ReadAsync(buffer,token) is var length&&length>0){if(data.Length+length>5242880)return;await data.WriteAsync(buffer.AsMemory(0,length),token);}
            token.ThrowIfCancellationRequested();
            // Virtualized rows can detach or switch URLs while decoding. Decode off
            // the UI thread and publish only to the row which still owns this request.
            var bitmap = await Task.Run(() => { data.Position = 0; return Bitmap.DecodeToWidth(data, 320); }, token);
            if (!_attached || token.IsCancellationRequested || _work != operation) { bitmap.Dispose(); return; }
            _bitmap = bitmap; _image.Source = bitmap; _fallback.IsVisible = false;
        }
        catch(Exception error) when(error is HttpRequestException or IOException or OperationCanceledException or ArgumentException){ }
        finally
        {
            if (_work == operation) _work = null;
            operation.Dispose();
        }
    }
}
