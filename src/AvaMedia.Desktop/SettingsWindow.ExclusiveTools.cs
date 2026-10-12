using Avalonia.Input;
using Avalonia.Interactivity;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private void RefreshExclusiveTools()
    {
        ExclusiveActivationPanel.IsVisible = !_settings.ExclusiveToolUnlocked;
        ExclusiveToolsPanel.IsVisible = _settings.ExclusiveToolUnlocked;
        ExclusiveActivationStatus.IsVisible = _settings.ExclusiveToolUnlocked;
        ExclusiveActivationStatus.Text = Localization.Text("专属工具已激活");
    }

    private void ActivateExclusiveToolClick(object? sender, RoutedEventArgs args) => ActivateExclusiveTool();
    private void ExclusivePasswordKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        args.Handled = true; ActivateExclusiveTool();
    }
    private void ActivateExclusiveTool()
    {
        if (_settings.ExclusiveToolUnlocked) return;
        if (ExclusivePasswordInput.Text != "147258")
        {
            ExclusiveActivationStatus.Text = Localization.Text("密码错误，请重试");
            ExclusiveActivationStatus.IsVisible = true;
            ExclusivePasswordInput.SelectAll(); ExclusivePasswordInput.Focus(); return;
        }
        try
        {
            _settings.ExclusiveToolUnlocked = true;
            if (Applied is null) new Storage().SaveSettings(_settings);
            else Applied.Invoke(this, EventArgs.Empty);
            _applied = true; ExclusivePasswordInput.Text = ""; RefreshExclusiveTools();
        }
        catch (Exception error)
        {
            _settings.ExclusiveToolUnlocked = false;
            RefreshExclusiveTools(); ExclusiveActivationStatus.Text = error.Message; ExclusiveActivationStatus.IsVisible = true;
        }
    }
    private async void OpenExclusiveRenamerClick(object? sender, RoutedEventArgs args)
    {
        if (!_settings.ExclusiveToolUnlocked) return;
        try
        {
            for (var owner = Owner; owner is not null; owner = owner.Owner)
                if (owner is MainWindow main) { await main.OpenExclusiveRenamerAsync(this); return; }
            await new ExclusiveRenamerWindow(new MediaEngine(_settings), _settings).ShowDialog(this);
        }
        catch (Exception error) { await Ui.Message(this, "专属重命名工具", error.Message); }
    }
}
