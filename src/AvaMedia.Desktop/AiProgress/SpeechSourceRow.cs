using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop.AiProgress;

// Only realized list rows probe media; detaching a virtualized row cancels its work.
internal sealed class SpeechSourceRow : Grid
{
    private static readonly SemaphoreSlim Slots = new(2);
    private readonly IMediaEngine _engine;
    private readonly string _path;
    private readonly CancellationToken _lifetime;
    private readonly TextBlock _metadata = SpeechUi.Text("", true, 12);
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private CancellationTokenSource? _work;
    private Bitmap? _bitmap;

    public SpeechSourceRow(IMediaEngine engine, string path, CancellationToken lifetime)
    {
        _engine = engine; _path = path; _lifetime = lifetime;
        ColumnDefinitions = new("64,*"); ColumnSpacing = 10; MinHeight = 40;
        var cover = SpeechUi.Card(_image, 0); cover.Width = 64; cover.Height = 40; cover.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("AcThumbRadius")); cover.ClipToBounds = true; Children.Add(cover);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = SpeechUi.Text(Path.GetFileName(path)); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        Localization.SetIsUserText(name, true); ToolTip.SetTip(this, path); texts.Children.Add(name); texts.Children.Add(_metadata); Grid.SetColumn(texts, 1); Children.Add(texts);
        AttachedToVisualTree += (_, _) => { _work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _ = LoadAsync(_work.Token); };
        DetachedFromVisualTree += (_, _) => { _work?.Cancel(); _work?.Dispose(); _work = null; _image.Source = null; _bitmap?.Dispose(); _bitmap = null; };
    }
    private async Task LoadAsync(CancellationToken token)
    {
        try
        {
            var result = await Task.Run(async () =>
            {
                await Slots.WaitAsync(token);
                try
                {
                    var info = await _engine.Probe(_path, token);
                    Bitmap? bitmap = null;
                    if (info.HasVideo)
                    {
                        try { var data = await _engine.Thumbnail(_path, 0, 128, 80, token); bitmap = new Bitmap(new MemoryStream(data)); }
                        catch (Exception error) when (error is not OperationCanceledException) { /* Metadata remains useful without a cover. */ }
                    }
                    return (info, bitmap);
                }
                finally { Slots.Release(); }
            }, token);
            if (token.IsCancellationRequested) { result.bitmap?.Dispose(); return; }
            _bitmap = result.bitmap; _image.Source = _bitmap;
            var info = result.info;
            var audio = !info.HasAudio ? Localization.Text("无音轨") : string.Join(" · ", new[] { info.AudioCodec.ToUpperInvariant(),
                info.AudioChannels > 0 ? Localization.Format($"{info.AudioChannels} 声道") : "" }.Where(x => x.Length > 0));
            _metadata.Text = AiActivity.FormatElapsed(info.Duration) + (audio.Length > 0 ? " · " + audio : "");
            Localization.SetIsUserText(_metadata, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) { _metadata.Text = Localization.Text("无法读取媒体信息"); ToolTip.SetTip(_metadata, error.Message); } }
    }
}
