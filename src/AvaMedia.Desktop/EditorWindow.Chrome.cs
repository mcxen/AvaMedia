using Avalonia;
using Avalonia.Controls;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

// Editing modes remain a business toolbar; the window caption belongs to the OS.
public partial class EditorWindow
{
    private void InitializeChrome()
    {
        ActualThemeVariantChanged += (_, _) => ApplyEditorFrame();
        Opened += (_, _) => ApplyEditorFrame();
        ApplyEditorFrame();
    }

    private void ApplyEditorFrame()
    {
        WindowChrome.ApplyPlatformFrame(this);
        var classic = Skin.UsesCustomChrome(ActualThemeVariant);
        QuickIdentityIcon.IsVisible = QuickIdentityTitle.IsVisible = classic;
        EditorTitleBar.Bind(MinHeightProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(classic ? "UiClassicToolbarHeight" : "UiEditorTitleBarHeight"));
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
