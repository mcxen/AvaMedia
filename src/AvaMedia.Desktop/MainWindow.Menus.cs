using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void ZoomWindowClick(object? sender, RoutedEventArgs args)
    {
        if (!CanResize || !CanMaximize) return;
        if (ActualThemeVariant == Skin.MacOS9) Skin.Zoom(this);
        else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ShadeWindowClick(object? sender, RoutedEventArgs args) => Skin.ToggleShade(this);

    private void MinimizeWindowClick(object? sender, RoutedEventArgs args)
    {
        if (CanMinimize) WindowState = WindowState.Minimized;
    }
}
