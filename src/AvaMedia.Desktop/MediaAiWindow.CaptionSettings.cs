using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private string? _captionSystemPrompt, _captionPrompt;
    private bool _captionUseFrameTools = true;

    private async Task OpenCaptionSettingsAsync()
    {
        if (_busy || _closed) return;
        var window = new Window { Title = "画面描述设置", Width = 700, Height = 660, MinWidth = 540, MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var system = Ui.Input(); system.AcceptsReturn = true; system.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        system.MaxLength = 8000; system.Text = _captionSystemPrompt ?? MediaCaptionService.DefaultSystemPrompt;
        var user = Ui.Input(); user.AcceptsReturn = true; user.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        user.MaxLength = 4000; user.Text = _captionPrompt ?? MediaCaptionService.DefaultUserPrompt;
        Localization.SetIsUserText(system, true); Localization.SetIsUserText(user, true);
        var tools = new CheckBox { Content = "视频按需补帧与局部放大", IsChecked = _captionUseFrameTools };
        ToolTip.SetTip(tools, "视觉模型需支持工具调用；最多补充 8 帧、2 轮。");
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,0.5*,Auto,Auto"), RowSpacing = 10, Margin = new(20) };
        root.Children.Add(Ui.Text("系统提示词")); Grid.SetRow(system, 1); root.Children.Add(system);
        var userLabel = Ui.Text("描述要求"); Grid.SetRow(userLabel, 2); root.Children.Add(userLabel);
        Grid.SetRow(user, 3); root.Children.Add(user); Grid.SetRow(tools, 4); root.Children.Add(tools);
        var actions = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        actions.Children.Add(Ui.DialogButton("恢复默认", () =>
        { system.Text = MediaCaptionService.DefaultSystemPrompt; user.Text = MediaCaptionService.DefaultUserPrompt; tools.IsChecked = true; }));
        var cancel = Ui.DialogButton("取消", window.Close); Grid.SetColumn(cancel, 2); actions.Children.Add(cancel);
        var save = Ui.DialogButton("保存", async () =>
        {
            try
            {
                var nextSystem = system.Text?.Trim(); var nextUser = user.Text?.Trim();
                MediaCaptionService.ValidateOptions(new(CaptionPrompt: nextUser) { CaptionSystemPrompt = nextSystem });
                _captionSystemPrompt = string.IsNullOrWhiteSpace(nextSystem) || nextSystem == MediaCaptionService.DefaultSystemPrompt ? null : nextSystem;
                _captionPrompt = string.IsNullOrWhiteSpace(nextUser) || nextUser == MediaCaptionService.DefaultUserPrompt ? null : nextUser;
                _captionUseFrameTools = tools.IsChecked == true;
                SavePreferences(); window.Close();
            }
            catch (Exception error) { await Ui.Message(window, "无法保存设置", error.Message); }
        });
        Grid.SetColumn(save, 3); actions.Children.Add(save); Grid.SetRow(actions, 5); root.Children.Add(actions);
        window.Content = root; await window.ShowDialog(_settingsOwner ?? this);
    }
}
