using Avalonia;
using Avalonia.Controls;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

// Editor title bar reuses the shared platform frame (Controls/WindowChrome) on modern skins;
// Mac OS 9 / XP skins draw their own frame, so the bar is then a plain toolbar row.
public partial class EditorWindow
{
    private StackPanel? _editorCaption;
    private Border[]? _editorGrips;
    private bool _editorOwnDecorations;

    private bool ModernFrame => !Skin.UsesCustomChrome(ActualThemeVariant) && MainWindow.FrameKind != MainWindow.WindowFrameKind.System;

    private void InitializeChrome()
    {
        WindowChrome.HookDrag(this, EditorTitleBar, () => ModernFrame);
        ActualThemeVariantChanged += (_, _) => ApplyEditorFrame();
        Opened += (_, _) => ApplyEditorFrame();
    }

    private void ApplyEditorFrame()
    {
        var modern = ModernFrame;
        if (!modern)
        {
            ExtendClientAreaToDecorationsHint = false;
            if (_editorOwnDecorations) { SystemDecorations = SystemDecorations.Full; _editorOwnDecorations = false; }
            SetEditorFrameParts(false, false);
            return;
        }
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 44;
        if (MainWindow.FrameKind == MainWindow.WindowFrameKind.Mac)
        {
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            EditorTitleText.Margin = new Thickness(72, 0, 0, 0); // room for the native traffic lights
            SetEditorFrameParts(false, false);
            return;
        }
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        var ownFrame = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        if (ownFrame) { SystemDecorations = SystemDecorations.None; _editorOwnDecorations = true; }
        SetEditorFrameParts(true, ownFrame);
    }

    private void SetEditorFrameParts(bool caption, bool grips)
    {
        if (caption && _editorCaption is null)
        {
            _editorCaption = WindowChrome.CreateCaptionButtons(this, out _);
            _editorCaption.Margin = new Thickness(8, -4, -8, 0);
            Grid.SetColumn(_editorCaption, 3);
            EditorTitleBar.Children.Add(_editorCaption);
        }
        if (_editorCaption is not null) _editorCaption.IsVisible = caption;
        if (grips && _editorGrips is null) _editorGrips = WindowChrome.AddResizeGrips(this, EditorLayout);
        foreach (var grip in _editorGrips ?? []) grip.IsVisible = grips;
    }

    private void RefreshInspector()
    {
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
