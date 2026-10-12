namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void InitializePlatinumPresentation()
    {
        ActualThemeVariantChanged += PlatinumPresentationChanged;
        Opened += PlatinumPresentationChanged;
        Closed += (_, _) => { ActualThemeVariantChanged -= PlatinumPresentationChanged; Opened -= PlatinumPresentationChanged; };
    }

    private void PlatinumPresentationChanged(object? sender, EventArgs args)
    {
        RefreshOutputPath();
    }
}
