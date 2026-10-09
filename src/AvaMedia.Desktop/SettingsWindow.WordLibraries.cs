using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private void InitializeWordLibraryManagement()
    {
        WordLibrariesTab.IsVisible = true;
    }
    private async void ManageWordLibraries(object? sender, RoutedEventArgs args)
    {
        await new WordLibraryWindow().ShowDialog(this);
    }
}
