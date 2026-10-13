using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private void InitializeExclusiveTools() => Ui.HookExclusiveToolsActivation(AboutAppIcon, this, () =>
    {
        var previous = _settings.ExclusiveToolUnlocked;
        try
        {
            _settings.ExclusiveToolUnlocked = true;
            if (Applied is null) new Storage().SaveSettings(_settings);
            else Applied.Invoke(this, EventArgs.Empty);
            _applied = true;
        }
        catch
        {
            _settings.ExclusiveToolUnlocked = previous;
            throw;
        }
    });
}
