using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private string? _captionSystemPrompt, _captionPrompt;
    private bool _captionUseFrameTools = true;
    private string? _captionLocalModelId = ModelCatalog.SummaryQwen35Id;
    private sealed record CaptionModelChoice(string? Id, string Name, bool UserText = false);

    private async Task OpenCaptionSettingsAsync()
    {
        if (_busy || _closed) return;
        var window = new Window { Title = "画面描述设置", Width = 700, Height = 720, MinWidth = 540, MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var choices = new[] { new CaptionModelChoice(null, "AI 供应商"),
            new CaptionModelChoice(ModelCatalog.SummaryQwen35Id, ModelCatalog.Find(ModelCatalog.SummaryQwen35Id).Name, true) };
        var model = new ComboBox { ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = choices.FirstOrDefault(choice => choice.Id == _captionLocalModelId) ?? choices[1] };
        model.ItemTemplate = new FuncDataTemplate<CaptionModelChoice>((choice, _) =>
        { var text = Ui.Text(choice?.Name ?? ""); Localization.SetIsUserText(text, choice?.UserText == true); return text; });
        var modelRow = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
        modelRow.Children.Add(Ui.Text("描述模型")); Grid.SetColumn(model, 1); modelRow.Children.Add(model);
        var manage = Ui.Button("模型管理…", async () => await ManageModelsAsync(window));
        Grid.SetColumn(manage, 2); modelRow.Children.Add(manage);
        var system = Ui.Input(); system.AcceptsReturn = true; system.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        system.MaxLength = 8000; system.Text = _captionSystemPrompt ?? MediaCaptionService.DefaultSystemPrompt;
        var user = Ui.Input(); user.AcceptsReturn = true; user.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        user.MaxLength = 4000; user.Text = _captionPrompt ?? MediaCaptionService.DefaultUserPrompt;
        Localization.SetIsUserText(system, true); Localization.SetIsUserText(user, true);
        var tools = new CheckBox { Content = "视频按需补帧与局部放大", IsChecked = _captionUseFrameTools };
        ToolTip.SetTip(tools, "视觉模型需支持工具调用；最多补充 8 帧、2 轮。");
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,0.5*,Auto,Auto"), RowSpacing = 10, Margin = new(20) };
        root.Children.Add(modelRow);
        var systemLabel = Ui.Text("系统提示词"); Grid.SetRow(systemLabel, 1); root.Children.Add(systemLabel);
        Grid.SetRow(system, 2); root.Children.Add(system);
        var userLabel = Ui.Text("描述要求"); Grid.SetRow(userLabel, 3); root.Children.Add(userLabel);
        Grid.SetRow(user, 4); root.Children.Add(user); Grid.SetRow(tools, 5); root.Children.Add(tools);
        var actions = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        actions.Children.Add(Ui.DialogButton("恢复默认", () =>
        { model.SelectedItem = choices[1]; system.Text = MediaCaptionService.DefaultSystemPrompt; user.Text = MediaCaptionService.DefaultUserPrompt; tools.IsChecked = true; }));
        var cancel = Ui.DialogButton("取消", window.Close); Grid.SetColumn(cancel, 2); actions.Children.Add(cancel);
        var save = Ui.DialogButton("保存", async () =>
        {
            try
            {
                var nextSystem = system.Text?.Trim(); var nextUser = user.Text?.Trim();
                var nextModel = (model.SelectedItem as CaptionModelChoice)?.Id;
                MediaCaptionService.ValidateOptions(new(CaptionPrompt: nextUser) { CaptionSystemPrompt = nextSystem, CaptionLocalModelId = nextModel });
                _captionSystemPrompt = string.IsNullOrWhiteSpace(nextSystem) || nextSystem == MediaCaptionService.DefaultSystemPrompt ? null : nextSystem;
                _captionPrompt = string.IsNullOrWhiteSpace(nextUser) || nextUser == MediaCaptionService.DefaultUserPrompt ? null : nextUser;
                _captionUseFrameTools = tools.IsChecked == true;
                _captionLocalModelId = nextModel;
                SavePreferences(); window.Close(); await RefreshModelAsync(prepare: false);
            }
            catch (Exception error) { await Ui.Message(window, "无法保存设置", error.Message); }
        });
        Grid.SetColumn(save, 3); actions.Children.Add(save); Grid.SetRow(actions, 6); root.Children.Add(actions);
        window.Content = root; await window.ShowDialog(_settingsOwner ?? this);
    }
}
