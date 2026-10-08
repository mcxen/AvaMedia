using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void WindowsXPClick(object? sender, RoutedEventArgs args) => SetSkin("WindowsXP");
    private void RefreshOutputPath() => OutputPath.Text = (ActualThemeVariant == Skin.WindowsXP ? "" : "📂 ") + _settings.OutputFolder;
}
