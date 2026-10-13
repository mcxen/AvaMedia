using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

/// <summary>Observe slider input even when its thumb handles or captures pointer events.</summary>
internal sealed class PreviewDrag(Slider slider, Action completed)
{
    public bool IsDragging { get; private set; }

    public void Attach()
    {
        slider.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) IsDragging = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Finish(),
            RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Finish(),
            RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(Thumb.DragCompletedEvent, (_, _) => Finish(),
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Finish()
    {
        if (!IsDragging) return;
        IsDragging = false; completed();
    }
}
