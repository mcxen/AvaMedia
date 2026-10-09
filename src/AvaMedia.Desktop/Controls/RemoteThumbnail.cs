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
    public RemoteThumbnail()
    {
        var root = new Grid(); root.Children.Add(_fallback); root.Children.Add(_image); Content = root;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if(change.Property == SourceUrlProperty) _ = LoadAsync(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); if(_bitmap is null) _ = LoadAsync(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _work?.Cancel();_work?.Dispose();_work=null; _image.Source=null; _bitmap?.Dispose();_bitmap=null; base.OnDetachedFromVisualTree(e); }
    private async Task LoadAsync()
    {
        _work?.Cancel();_work?.Dispose();var operation = new CancellationTokenSource();_work=operation;var token=operation.Token;
        _image.Source=null;_bitmap?.Dispose();_bitmap=null;_fallback.IsVisible=true;
        if(!Uri.TryCreate(SourceUrl,UriKind.Absolute,out var uri)||uri.Scheme is not ("https" or "http"))return;
        try
        {
            using var response=await Client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is > 5242880)return;
            await using var input=await response.Content.ReadAsStreamAsync(token);using var data=new MemoryStream();var buffer=new byte[16384];
            while(await input.ReadAsync(buffer,token) is var length&&length>0){if(data.Length+length>5242880)return;await data.WriteAsync(buffer.AsMemory(0,length),token);}
            token.ThrowIfCancellationRequested();if(_work!=operation)return;data.Position=0;
            _bitmap=Bitmap.DecodeToWidth(data,320);_image.Source=_bitmap;_fallback.IsVisible=false;
        }
        catch(Exception error) when(error is HttpRequestException or IOException or OperationCanceledException or ArgumentException){ }
    }
}
