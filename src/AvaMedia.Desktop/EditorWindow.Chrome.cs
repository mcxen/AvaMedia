using Avalonia;
using Avalonia.Controls;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

// Editor title bar reuses the shared platform frame (Controls/WindowChrome) on modern skins;
// Mac OS 9 / XP skins draw their own frame, so the bar is then a plain toolbar row.
public partial class EditorWindow
{
    private StackPanel? _editorCaption;

    private bool ModernFrame => !Skin.UsesCustomChrome(ActualThemeVariant) && MainWindow.FrameKind != MainWindow.WindowFrameKind.System;

    private void InitializeChrome()
    {
        WindowChrome.HookDrag(this, EditorTitleBar, () => ModernFrame);
        ActualThemeVariantChanged += (_, _) => ApplyEditorFrame();
        Opened += (_, _) => ApplyEditorFrame();
    }

    private void ApplyEditorFrame()
    {
        WindowChrome.ApplyPlatformFrame(this);
        var mac = ModernFrame && MainWindow.FrameKind == MainWindow.WindowFrameKind.Mac;
        EditorTitleText.Margin = new Thickness(mac ? 72 : 0, 0, 0, 0);
        QuickTitleBar.Margin = mac ? new Thickness(70, 0, 70, 0) : new Thickness(0);
        SetEditorFrameParts(WindowChrome.UsesClientTitleBar(this) && !mac);
    }

    private void SetEditorFrameParts(bool caption)
    {
        if (caption && _editorCaption is null)
        {
            _editorCaption = WindowChrome.CreateCaptionButtons(this, out _);
            _editorCaption.Margin = new Thickness(8, -4, -8, 0);
            if (QuickRoot.IsVisible) { _editorCaption.Margin = new Thickness(6, 0, 0, 0); QuickCaptionSlot.Children.Add(_editorCaption); }
            else { Grid.SetColumn(_editorCaption, 3); EditorTitleBar.Children.Add(_editorCaption); }
        }
        if (_editorCaption is not null) _editorCaption.IsVisible = caption;
    }

    private void RefreshInspector()
    {
        RefreshQuickLayout();
        FooterOptionsButton.IsVisible = QuickWorkflow;
        var segment = _activeSegment;
        InspectorTitle.Text = segment?.Title ?? "";
        InspectorDetails.Text = segment is null ? "" : string.Join("\n", new[]
        {
            segment.TimeRange, segment.DurationLabel,
            Localization.Format($"淡入 {Localization.Text(FadeInCombo.SelectionBoxItem?.ToString() ?? "")} · 淡出 {Localization.Text(FadeOutCombo.SelectionBoxItem?.ToString() ?? "")}"),
            segment.HasEdits ? segment.EditSummary : Localization.Text("无裁剪 / 旋转")
        });
    }
}
