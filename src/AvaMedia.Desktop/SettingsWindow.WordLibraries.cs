using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private async void ManageWordLibraries(object? sender, RoutedEventArgs args)
    {
        await WordLibraryWindow.ShowSingleAsync(settings: _settings);
    }
}
