using Avalonia.Threading;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    /// <summary>Runs one organize action requested from the task detail window after the task finished loading.</summary>
    internal async Task RunTaskActionAsync(string action)
    {
        while (_loadingTask || _refreshingSnapshot) { await _snapshotRefresh; await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }
        if (_closed) return;
        if (action.EndsWith("-move", StringComparison.Ordinal)) _mode.SelectedIndex = 1;
        else if (action is "preview" or "organize") _mode.SelectedIndex = 0;
        switch (action)
        {
            case "undo": await GuardAsync(UndoAsync); break;
            case "export": await GuardAsync(ExportAsync); break;
            case "preview" or "preview-move": await GuardAsync(PreviewAsync); break;
            case "organize" or "organize-move":
                await GuardAsync(PreviewAsync);
                if (_plan is { Length: > 0 }) await GuardAsync(OrganizeAsync);
                break;
        }
    }
}
