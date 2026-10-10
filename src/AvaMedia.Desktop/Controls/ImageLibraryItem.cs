using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Visible library rows own their thumbnail and cancel work when virtualized away.</summary>
internal sealed class ImageLibraryItem : Grid
{
    private readonly Image _image = new() { Width = 48, Height = 44, Stretch = Stretch.Uniform };
    private CancellationTokenSource? _request;
    private Bitmap? _bitmap;
    public ImageLibraryItem(ImageViewerEntry entry, Func<ImageViewerEntry, CancellationToken, Task<byte[]>> read)
    {
        ColumnDefinitions = new("48,*"); ColumnSpacing = 6; Children.Add(_image);
        var text = Ui.Text(entry.Name); Localization.SetIsUserText(text, true);
        text.MaxLines = 2; text.TextTrimming = TextTrimming.CharacterEllipsis; text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1); Children.Add(text); ToolTip.SetTip(text, entry.Identity);
        AttachedToVisualTree += async (_, _) =>
        {
            using var request = new CancellationTokenSource(); _request = request;
            try
            {
                await Task.Delay(150, request.Token);
                var png = await read(entry, request.Token); request.Token.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(png); _bitmap = Bitmap.DecodeToWidth(stream, 96); _image.Source = _bitmap;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally { if (ReferenceEquals(_request, request)) _request = null; }
        };
        DetachedFromVisualTree += (_, _) => { _request?.Cancel(); _image.Source = null; _bitmap?.Dispose(); _bitmap = null; };
    }
}
