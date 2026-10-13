using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace AvaMedia.Desktop;

public sealed partial class ContactSheetWindow
{
    private bool _previewMotion;

    private void InitializePreviewMotion()
    {
        Motion.PreferencesChanged += RefreshPreviewMotion;
        ActualThemeVariantChanged += (_, _) => RefreshPreviewMotion();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty || args.Property == WindowStateProperty)
                RefreshPreviewMotion();
        };
        Closed += (_, _) => { Motion.PreferencesChanged -= RefreshPreviewMotion; StopPreviewMotion(); };
    }

    private void StartPreviewMotion() { _previewMotion = true; RefreshPreviewMotion(); }
    private void StopPreviewMotion()
    {
        _previewMotion = false;
        foreach (var cell in _cells) StopCellMotion(cell);
    }

    private void RefreshPreviewMotion()
    {
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (!_previewMotion || _closed || !cell.Loading || !Motion.CanAnimate(cell.Face)
                || Skin.UsesCustomChrome(ActualThemeVariant)) { StopCellMotion(cell); continue; }
            if (cell.PulseVisual is not null || ElementComposition.GetElementVisual(cell.Face) is not { } visual) continue;
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromSeconds(2);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            // Preserve the staggered pulse using compositor keyframes. No UI timer
            // updates every cell's Opacity while frames are being decoded.
            for (var frame = 0; frame <= 16; frame++)
                animation.InsertKeyFrame(frame / 16f, (float)(.82 + .18 * Math.Sin(frame / 16d * Math.PI * 2 - i * .45)));
            cell.PulseVisual = visual;
            visual.StartAnimation("Opacity", animation);
        }
    }

    private static void StopCellMotion(PreviewCell cell)
    {
        if (cell.PulseVisual is { } visual)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            cell.PulseVisual = null;
        }
        cell.Face.Opacity = 1;
    }
}
