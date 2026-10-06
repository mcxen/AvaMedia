namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void InitializePlatinumPresentation()
    {
        ActualThemeVariantChanged += PlatinumPresentationChanged;
        Opened += PlatinumPresentationChanged;
        Closed += (_, _) => { ActualThemeVariantChanged -= PlatinumPresentationChanged; Opened -= PlatinumPresentationChanged; };
        RefreshFeatureMetrics();
    }

    private void PlatinumPresentationChanged(object? sender, EventArgs args) => RefreshFeatureMetrics();
}
