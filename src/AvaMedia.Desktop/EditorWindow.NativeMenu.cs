using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public partial class EditorWindow
{
    internal bool CanNativeUndo => !_closed && !_restoringSegments && UndoSegmentButton.IsEnabled;
    internal bool CanNativeRedo => !_closed && !_restoringSegments && RedoSegmentButton.IsEnabled;
    internal void NativeUndoRedo(bool redo)
    {
        if (redo) RedoSegmentClick(this, new RoutedEventArgs());
        else UndoSegmentClick(this, new RoutedEventArgs());
    }
}
