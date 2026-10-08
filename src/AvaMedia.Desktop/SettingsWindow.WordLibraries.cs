using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private void InitializeWordLibraryManagement()
    {
        WordLibrariesTab.IsVisible = _settings.EnableBetaFeatures;
        BetaInput.IsCheckedChanged += (_, _) => WordLibrariesTab.IsVisible = BetaInput.IsChecked == true;
    }
    private async void ManageWordLibraries(object? sender, RoutedEventArgs args)
    {
        if (BetaInput.IsChecked == true) await new WordLibraryWindow().ShowDialog(this);
    }
}
