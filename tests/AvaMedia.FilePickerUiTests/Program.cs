using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Desktop;
using AvaMedia.FileUi;

AppBuilder.Configure<PickerTestApp>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

var owners = new[] { new Window(), new Window() };
foreach (var owner in owners) owner.Show();
Pump();
try
{
    var source = new PickerSource();
    FileBrowserView? previous = null;
    // Exercise the production dialog construction and close handlers, alternating owner windows.
    for (var open = 0; open < 6; open++)
    {
        var historyRequests = source.HistoryRequests;
        var mediaRequests = source.MediaTokens.Count;
        var options = new FileBrowserOptions("选择文件", Multiple: true, StartFolder: source.Folder);
        var view = new FileBrowserView(source, options);
        var dialog = ToolFilePicker.CreateDialog(view, options);
        Check(!ReferenceEquals(view, previous), "Each open uses a new browser");
        AssertParents(view, dialog);
        var result = dialog.ShowDialog<object?>(owners[open % owners.Length]);
        PumpUntil(() => source.HistoryRequests > historyRequests && view.CurrentFolder == source.Folder && Entries(view).ItemCount > 0);
        Check(ReferenceEquals(dialog.Owner, owners[open % owners.Length]), "Dialog has the requested owner");
        Check(view.GridMode == source.Grid, "Opening restores the shared mode preference");
        for (var change = 0; change < 4; change++)
        {
            var grid = change % 2 != 0;
            Pump(view.SetGridModeAsync(grid));
            Check(view.GridMode == grid && source.Grid == grid, "Grid/list switch updates the view and preference");
            Check(Entries(view).ItemCount == (grid ? 2 : source.Items.Count), "Grid/list switch rebuilds virtualized rows");
            AssertParents(view, dialog);
        }
        // Leave a different preference for the next instance to restore.
        Pump(view.SetGridModeAsync(open % 2 == 0));
        PumpUntil(() => source.MediaTokens.Count > mediaRequests);
        if (open % 2 == 0)
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else dialog.Close();
        Pump(result);
        Check(!dialog.IsVisible && !view.IsAttachedToVisualTree(), "Close/cancel detaches the browser");
        Check(source.MediaTokens.Count > 0 && source.MediaTokens.All(token => token.IsCancellationRequested),
            "Close releases all visible media work");
        previous = view;
    }
    Console.WriteLine("PASS repeated open/close, different owners, grid/list switches and logical parents");

    // Two owners may have independent pickers alive at the same time.
    var options1 = new FileBrowserOptions("窗口一", StartFolder: source.Folder);
    var options2 = new FileBrowserOptions("窗口二", StartFolder: source.Folder);
    var first = new FileBrowserView(new PickerSource(), options1);
    var second = new FileBrowserView(new PickerSource(), options2);
    var firstDialog = ToolFilePicker.CreateDialog(first, options1);
    var secondDialog = ToolFilePicker.CreateDialog(second, options2);
    var firstResult = firstDialog.ShowDialog<object?>(owners[0]);
    var secondResult = secondDialog.ShowDialog<object?>(owners[1]);
    try
    {
        PumpUntil(() => Entries(first).ItemCount > 0 && Entries(second).ItemCount > 0);
        Pump(first.SetGridModeAsync(false));
        Check(!first.GridMode && second.GridMode, "Concurrent pickers have independent modes");
        AssertParents(first, firstDialog); AssertParents(second, secondDialog);
        firstDialog.Close(); Pump(firstResult);
        Check(secondDialog.IsVisible && second.IsAttachedToVisualTree(), "Closing one picker keeps the other attached");
        Pump(second.SetGridModeAsync(false)); Pump(second.SetGridModeAsync(true));
        AssertParents(second, secondDialog);
    }
    finally { firstDialog.Close(); secondDialog.Close(); Pump(firstResult); Pump(secondResult); }
    Console.WriteLine("PASS simultaneous pickers in different windows");
}
finally { foreach (var owner in owners) owner.Close(); Pump(); }

static ListBox Entries(FileBrowserView view) => view.GetVisualDescendants().OfType<ListBox>().Single();
static void AssertParents(FileBrowserView view, Window dialog)
{
    Check(view.Parent is Grid parent && parent.Children.Contains(view), "Browser belongs to its chrome grid");
    Check(view.GetLogicalAncestors().Contains(dialog), "Browser has an unbroken logical path to the dialog");
    var navigation = dialog.GetLogicalDescendants().OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == "列表视图");
    Check(navigation.GetLogicalAncestors().Contains(dialog) && !navigation.GetLogicalAncestors().Contains(view),
        "Extracted navigation belongs to the chrome, outside the browser");
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Pump(Task? task = null)
{
    PumpUntil(() => task is null || task.IsCompleted);
    task?.GetAwaiter().GetResult();
}
static void PumpUntil(Func<bool> complete)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    do
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        if (complete()) return;
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Picker UI did not finish its pending work.");
        Thread.Sleep(5);
    } while (true);
}

public sealed class PickerTestApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}

internal sealed class PickerSource : IFileBrowserSource
{
    public FileBrowserItem Folder { get; } = new("媒体", "picker:media", true);
    public IReadOnlyList<FileBrowserEntry> Items { get; } = Enumerable.Range(0, 6)
        .Select(index => new FileBrowserEntry(new($"文件 {index}.mp4", $"picker:file-{index}", false), 100, null)).ToArray();
    public bool Grid { get; private set; } = true;
    public int HistoryRequests { get; private set; }
    public System.Collections.Concurrent.ConcurrentBag<CancellationToken> MediaTokens { get; } = [];
    public Task<IReadOnlyList<FileBrowserLocation>> GetLocationsAsync(CancellationToken token)
        => Task.FromResult<IReadOnlyList<FileBrowserLocation>>([new("媒体", Folder)]);
    public Task<FileBrowserItem?> ResolveAsync(string address, CancellationToken token)
        => Task.FromResult<FileBrowserItem?>(Folder);
    public Task<FileBrowserDirectory> ReadAsync(FileBrowserItem folder, CancellationToken token)
        => Task.FromResult(new FileBrowserDirectory(folder, null, Items));
    public string DisplayAddress(FileBrowserItem item) => item.Address;
    public Task<bool> GetGridModeAsync(CancellationToken token) => Task.FromResult(Grid);
    public Task<FileBrowserHistory> GetHistoryAsync(CancellationToken token)
    {
        HistoryRequests++;
        return Task.FromResult(new FileBrowserHistory([], [], []));
    }
    public Task SetGridModeAsync(bool grid, CancellationToken token) { Grid = grid; return Task.CompletedTask; }
    public async Task<FileBrowserMedia?> GetMediaAsync(FileBrowserEntry entry, CancellationToken token)
    {
        MediaTokens.Add(token);
        await Task.Delay(Timeout.Infinite, token);
        return null;
    }
}
