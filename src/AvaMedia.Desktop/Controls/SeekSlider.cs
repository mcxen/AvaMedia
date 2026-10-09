using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop.Controls;

/// <summary>Seeks directly to the pointer while retaining Slider's skin and keyboard controls.</summary>
public sealed class SeekSlider : Slider
{
    protected override Type StyleKeyOverride => typeof(Slider);
    private Track? _track;
    private bool _seeking;

    public SeekSlider()
    {
        AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => _seeking = false;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e); _track = e.NameScope.Find<Track>("PART_Track");
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled || _track is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); _seeking = true; e.Pointer.Capture(this); Seek(e); e.Handled = true;
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (!_seeking) return;
        Seek(e); e.Handled = true;
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seeking) return;
        Seek(e); _seeking = false; e.Pointer.Capture(null); e.Handled = true;
    }

    private void Seek(PointerEventArgs e)
    {
        if (_track is null) return;
        var value = _track.ValueFromPoint(e.GetPosition(_track));
        if (double.IsFinite(value)) SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }
}
