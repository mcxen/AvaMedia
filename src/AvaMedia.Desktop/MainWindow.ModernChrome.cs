using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <summary>
/// Light / dark skins use a macOS-style shell: floating sidebar, title + capsule groups above the
/// task list and an inline status bar. The same command controls are re-parented, so handlers,
/// enabled state and automation names stay shared with the classic Mac OS 9 / XP toolbar.
/// </summary>
public partial class MainWindow
{
    private Control[]? _toolbarOrder;
    private Control? _statusBar;
    private Panel? _statusHome;
    private MenuFlyout? _appMenuFlyout;
    private bool? _modernChrome;

    internal bool ModernChrome => _modernChrome == true;

    private void InitializeModernChrome()
    {
        _toolbarOrder = ToolbarItems.Children.ToArray();
        _statusBar = StatusBarPanel;
        _statusHome = _statusBar?.Parent as Panel;
        AppMenuButton.Click += (_, _) => _appMenuFlyout?.ShowAt(AppMenuButton);
        Classes.CollectionChanged += (_, _) => ApplyChrome();
        ApplyChrome();
    }

    private void ApplyChrome()
    {
        var modern = !Classes.Contains("mac-os9") && !Classes.Contains("windows-xp");
        if (_modernChrome == modern || _toolbarOrder is null) return;
        _modernChrome = modern;
        foreach (var control in _toolbarOrder) (control.Parent as Panel)?.Children.Remove(control);
        if (modern)
        {
            foreach (var button in new Control[] { RouteButton, OutputFolderButton, OptionsButton, WifiButton }) Cap(ModernToolCapsule, button);
            foreach (var button in new Control[] { EditTaskButton, RemoveButton, ClearButton }) Cap(ModernEditCapsule, button);
            Cap(ModernRunCapsule, StopButton); Cap(ModernRunCapsule, StartButton);
            MoveMenu(toFlyout: true);
            if (_statusBar is not null) { _statusHome?.Children.Remove(_statusBar); ModernStatusHost.Child = _statusBar; }
        }
        else
        {
            foreach (var control in _toolbarOrder) { control.Classes.Remove("cap"); ToolbarItems.Children.Add(control); }
            MoveMenu(toFlyout: false);
            if (_statusBar is not null && _statusHome is not null) { ModernStatusHost.Child = null; _statusHome.Children.Add(_statusBar); }
        }
        ApplicationMenu.IsVisible = AppToolbar.IsVisible = !modern;
        ModernTitleBar.IsVisible = ModernQueueBar.IsVisible = modern;
        Grid.SetRowSpan(WorkspaceLayout, modern ? 2 : 1);
        // The modern status bar gives the queue summary its natural width instead of trimming it.
        if (SummaryText.Parent is Grid status)
        {
            // All skins: the queue summary keeps its natural width; the output path takes the rest.
            status.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            status.ColumnDefinitions[2].Width = GridLength.Auto;
            status.ColumnDefinitions[0].MinWidth = 150;
            SummaryText.TextTrimming = TextTrimming.None;
            status.ColumnDefinitions[6].Width = modern ? GridLength.Auto : new GridLength(142);
        }
        FeatureSidebar.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(modern ? "ShellSidebar" : "UiFeaturePanel"));
        ApplyWindowFrame(modern);
        RefreshTaskList();
        if (_visibleJobs.Count > 0) JobDisplayChanged?.Invoke();
    }

    private static void Cap(Panel capsule, Control control)
    {
        control.Classes.Add("cap"); capsule.Children.Add(control);
    }

    private void MoveMenu(bool toFlyout)
    {
        if (toFlyout)
        {
            var items = ApplicationMenu.Items.OfType<MenuItem>().ToArray();
            ApplicationMenu.Items.Clear();
            _appMenuFlyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var item in items) _appMenuFlyout.Items.Add(item);
        }
        else if (_appMenuFlyout is not null)
        {
            var items = _appMenuFlyout.Items.OfType<MenuItem>().ToArray();
            _appMenuFlyout.Items.Clear(); _appMenuFlyout = null;
            foreach (var item in items) ApplicationMenu.Items.Add(item);
        }
    }

    private void RefreshTaskSummary()
    {
        var running = _jobs.Count(job => job.State is JobState.Running or JobState.Stopping);
        var waiting = _jobs.Count(job => job.State is JobState.Waiting or JobState.Paused);
        var failed = _jobs.Count(job => job.State == JobState.Failed);
        Localization.SetText(TaskSummaryText, $"{running} 个进行中 · {waiting} 个等待 · {failed} 个失败");
    }
}
