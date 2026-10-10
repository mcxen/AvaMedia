using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private async void ManageWordLibraries(object? sender, RoutedEventArgs args)
    {
        await new WordLibraryWindow(settings: _settings).ShowDialog(this);
    }
}
