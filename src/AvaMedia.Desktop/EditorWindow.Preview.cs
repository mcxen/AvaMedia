using Avalonia.Controls;
using Avalonia.Input;

namespace AvaMedia.Desktop;

public partial class EditorWindow
{
    private void BoundaryPreviewDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is not Button { Content: Image image }) return;
        args.Handled = true;
        var caption = ReferenceEquals(image, StartImage)
            ? Localization.Format($"开始时间 · {StartTime.Text}")
            : Localization.Format($"结束时间 · {EndTime.Text}");
        ImageViewerWindow.ShowPreview(this, image.Source, caption);
    }
}
