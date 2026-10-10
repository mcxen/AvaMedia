using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private void PrivacyChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        _activity.Update(null); _operation?.Cancel(); ReloadWordCandidates();
        UpdatePrivacyScopes(); RefreshDisplayedResults(); UpdateModelPreparationActions();
        _status.Text = Localization.Text("隐私设置已更新");
        _ = RefreshModelAsync();
    }

    private void UpdatePrivacyScopes()
    {
        var index = _tagScope.SelectedIndex;
        var names = new[] { "全部标签", "成人内容（NSFW）", "场景", "人物特征", "姿态 / 体位" };
        _tagScope.ItemsSource = names.Select((name, position) => new ComboBoxItem
        {
            Content = Localization.Text(position == 4 && !_settings.EnableNsfwContent ? "人物姿势" : name),
            IsVisible = position != 1 || _settings.EnableNsfwContent
        }).ToArray();
        _tagScope.SelectedIndex = index == 1 && !_settings.EnableNsfwContent ? 0 : Math.Max(0, index);
    }
}
