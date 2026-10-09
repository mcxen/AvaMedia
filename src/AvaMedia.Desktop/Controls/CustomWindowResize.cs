using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop.Controls;

/// <summary>Shared edge hit testing and drag resizing for the custom window skins.</summary>
internal sealed class CustomWindowResize : IDisposable
{
    private const double EdgeSize = 8;
    private const double CornerSize = 20;
    private static readonly Cursor VerticalCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor HorizontalCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor DownwardCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor UpwardCursor = new(StandardCursorType.TopRightCorner);
    private readonly Window _window;
    private readonly Control _frame;
    private readonly Control _grip;
    private readonly Func<bool> _canResize;
    private IPointer? _pointer;
    private WindowEdge _edge;
    private PixelPoint _origin;
    private PixelPoint _position;
    private Size _size;
    private double _scaling;

    public CustomWindowResize(Window window, Control frame, Control grip, Func<bool>? canResize = null)
    {
        _window = window; _frame = frame; _grip = grip;
        _canResize = canResize ?? (() => true);
        frame.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        frame.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, handledEventsToo: true);
        frame.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel, handledEventsToo: true);
        frame.PointerCaptureLost += CaptureLost;
        frame.PointerExited += Exited;
        window.PropertyChanged += WindowChanged;
        window.Deactivated += Deactivated;
    }

    private bool CanResize => _window.IsVisible && _window.CanResize && _window.WindowState == WindowState.Normal && _canResize();

    private WindowEdge? Edge(PointerEventArgs args)
    {
        if (!CanResize) return null;
        if (_grip.IsVisible && args.Source is Visual source && (ReferenceEquals(source, _grip) || source.GetVisualAncestors().Contains(_grip)))
            return WindowEdge.SouthEast;
        // Caption buttons retain their full click target, including the top edge.
        if (args.Source is Visual visual && visual.GetVisualAncestors().Prepend(visual).OfType<Button>().Any()) return null;
        var point = args.GetPosition(_frame);
        var width = _frame.Bounds.Width; var height = _frame.Bounds.Height;
        if (point.X < 0 || point.Y < 0 || point.X > width || point.Y > height) return null;
        var left = point.X < EdgeSize; var right = point.X >= width - EdgeSize;
        var top = point.Y < EdgeSize; var bottom = point.Y >= height - EdgeSize;
        if (!(left || right || top || bottom)) return null;
        var cornerLeft = point.X < CornerSize; var cornerRight = point.X >= width - CornerSize;
        var cornerTop = point.Y < CornerSize; var cornerBottom = point.Y >= height - CornerSize;
        if (cornerTop && cornerLeft) return WindowEdge.NorthWest;
        if (cornerTop && cornerRight) return WindowEdge.NorthEast;
        if (cornerBottom && cornerLeft) return WindowEdge.SouthWest;
        if (cornerBottom && cornerRight) return WindowEdge.SouthEast;
        return top ? WindowEdge.North : bottom ? WindowEdge.South : left ? WindowEdge.West : WindowEdge.East;
    }

    private void Pressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_frame).Properties.IsLeftButtonPressed || Edge(args) is not { } edge) return;
        args.Handled = true;
        if (!OperatingSystem.IsMacOS())
        {
            _window.BeginResizeDrag(edge, args);
            return;
        }
        // Avalonia.Native 11.3.22 leaves BeginResizeDrag empty on macOS.
        // Screen coordinates use DesktopScaling, independently of Retina render scaling.
        _edge = edge; _position = _window.Position; _size = _window.ClientSize;
        _scaling = _window.DesktopScaling;
        _origin = _frame.PointToScreen(args.GetPosition(_frame));
        _window.SizeToContent = SizeToContent.Manual;
        _pointer = args.Pointer;
        SetCursor(edge);
        args.Pointer.Capture(_frame);
    }

    private void Moved(object? sender, PointerEventArgs args)
    {
        if (_pointer is null) { SetCursor(Edge(args)); return; }
        if (!ReferenceEquals(args.Pointer, _pointer)) return;
        if (!CanResize || !args.GetCurrentPoint(_frame).Properties.IsLeftButtonPressed) { End(); return; }
        var current = _frame.PointToScreen(args.GetPosition(_frame));
        var dx = (current.X - _origin.X) / _scaling;
        var dy = (current.Y - _origin.Y) / _scaling;
        var left = _edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
        var right = _edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
        var top = _edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;
        var bottom = _edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;
        var width = Math.Clamp(_size.Width + (left ? -dx : right ? dx : 0), Math.Max(1, _window.MinWidth), _window.MaxWidth);
        var height = Math.Clamp(_size.Height + (top ? -dy : bottom ? dy : 0), Math.Max(1, _window.MinHeight), _window.MaxHeight);
        _window.Width = width; _window.Height = height;
        _window.UpdateLayout();
        if (left || top)
        {
            // Use the size accepted by the platform so the opposite edge remains fixed at the limits.
            var actual = _window.ClientSize;
            _window.Position = new(_position.X + (left ? (int)Math.Round((_size.Width - actual.Width) * _scaling) : 0),
                _position.Y + (top ? (int)Math.Round((_size.Height - actual.Height) * _scaling) : 0));
        }
        args.Handled = true;
    }

    private void Released(object? sender, PointerReleasedEventArgs args)
    {
        if (!ReferenceEquals(args.Pointer, _pointer) || args.InitialPressMouseButton != MouseButton.Left) return;
        End(); args.Handled = true;
    }

    private void CaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        if (ReferenceEquals(args.Pointer, _pointer)) End();
    }

    private void Exited(object? sender, PointerEventArgs args) { if (_pointer is null) SetCursor(null); }
    private void Deactivated(object? sender, EventArgs args) => End();
    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if ((args.Property == Window.CanResizeProperty || args.Property == Window.WindowStateProperty || args.Property == Visual.IsVisibleProperty) && !CanResize)
            End();
    }

    private void SetCursor(WindowEdge? edge) => _frame.Cursor = edge switch
    {
        WindowEdge.North or WindowEdge.South => VerticalCursor,
        WindowEdge.West or WindowEdge.East => HorizontalCursor,
        WindowEdge.NorthWest or WindowEdge.SouthEast => DownwardCursor,
        WindowEdge.NorthEast or WindowEdge.SouthWest => UpwardCursor,
        _ => null
    };

    private void End()
    {
        var pointer = _pointer; _pointer = null;
        if (pointer?.Captured == _frame) pointer.Capture(null);
        SetCursor(null);
    }

    public void Dispose()
    {
        End();
        _frame.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
        _frame.RemoveHandler(InputElement.PointerMovedEvent, Moved);
        _frame.RemoveHandler(InputElement.PointerReleasedEvent, Released);
        _frame.PointerCaptureLost -= CaptureLost;
        _frame.PointerExited -= Exited;
        _window.PropertyChanged -= WindowChanged;
        _window.Deactivated -= Deactivated;
    }
}
