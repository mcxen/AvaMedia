using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

internal static partial class Program
{
    private static async Task CaptureTaskListStages()
    {
        var sample = "/workspace/AvaMedia-sidebar-review/ambient/samples/航天员.mp4";
        if (!File.Exists(sample)) throw new FileNotFoundException("Missing task-list stage sample", sample);
        var state = new Storage(Path.Combine(_output, "state", "tasklist"));
        var settings = new AppSettings
        {
            Theme = "Dark", Language = "zh-CN", CheckForUpdates = false, AutoUpdate = false,
            ReduceMotion = true, CollapseCompletedTasks = false, EnableBetaFeatures = true,
            OutputFolder = Path.Combine(_output, "output")
        };
        state.SaveSettings(settings);
        var fixtures = TaskListStageFixtures.CreateJobs(sample, Path.Combine(_output, "fixtures"));
        state.SaveJobs(fixtures);

        var main = new MainWindow(state, loadedSettings: settings) { Width = 1440, Height = 900 };
        main.Show();
        await Settle(main);
        var rows = main.GetVisualDescendants().OfType<JobRowView>().ToArray();
        await Task.WhenAll(rows.Select(row => row.Ready)).WaitAsync(TimeSpan.FromSeconds(40));
        var jobs = ((IEnumerable<Job>)(typeof(MainWindow).GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(main) ?? throw new MissingFieldException(typeof(MainWindow).FullName, "_jobs"))).ToArray();
        TaskListStageFixtures.Apply(jobs);
        RefreshQueueForCapture(main);
        await Settle(main);
        await Task.Delay(1200);
        TaskListStageFixtures.Apply(jobs);
        RefreshQueueForCapture(main);
        await Settle(main);
        // Grouping into 进行中 / 排队 / 已完成 recycles row containers; look the rows up again.
        rows = main.GetVisualDescendants().OfType<JobRowView>().ToArray();
        await Task.WhenAll(rows.Select(row => row.Ready)).WaitAsync(TimeSpan.FromSeconds(40));
        await Capture(main, "impl-tasklist-dark");
        var toggleCompleted = typeof(MainWindow).GetMethod("ToggleCompletedTasks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "ToggleCompletedTasks");
        toggleCompleted.Invoke(main, null);
        await Settle(main);
        await Capture(main, "impl-tasklist-collapsed-dark");
        toggleCompleted.Invoke(main, null);
        await Settle(main);

        // Hover affordances: full-row highlight and the drag grip at the thumbnail's bottom-right.
        var hoverRow = rows.Single(row => JobName(row) == "旅行vlog_成都.mp4");
        hoverRow.PreviewHover(true);
        await Settle(main);
        await Capture(main, "impl-hover-grip-dark");
        // Drag ghost and drop-target feedback (outline + hint) on the first visible tool that accepts the input.
        var hoverJob = (Job)hoverRow.DataContext!;
        main.PreviewFeatureDrop("video-compress", hoverJob, hoverRow.Details?.Cover, 3);
        await Settle(main);
        await Capture(main, "impl-drag-ghost-dark");
        main.PreviewFeatureDrop(null, hoverJob, null, 0);
        hoverRow.PreviewHover(false);

        await CaptureStageHover(main, "我做了20年的拿手菜.mp4", "impl-hover-running-dark");
        var failedRow = await CaptureStageHover(main, "访谈_第3集.mp4", "impl-hover-failed-dark");
        var failedJob = (Job)failedRow.DataContext!;
        var list = main.FindControl<ListBox>("JobList") ?? throw new InvalidOperationException("JobList not found");
        list.SelectedItems?.Clear();
        list.SelectedItems?.Add(failedJob);
        await Settle(main);
        // Leave room beside the task list for Avalonia's popup while keeping it on our display.
        main.Width = 1160; main.Height = 900; main.Position = new PixelPoint(0, 0);
        await Settle(main);
        var contextMenu = list.ContextMenu;
        if (contextMenu is not null)
        {
            contextMenu.PlacementTarget = failedRow;
            contextMenu.Placement = PlacementMode.Left;
            contextMenu.Open(list);
            await Task.Delay(500);
            await CaptureWindowAndPopups(main, "impl-context-retry-dark");
            contextMenu.Close();
        }
        else Console.WriteLine("Context menu unavailable; skipped context capture.");

        main.Close();
    }

    private static void RefreshQueueForCapture(MainWindow main)
    {
        // Fixtures set Job.State directly, so refresh the queue summary and completed-task group too.
        var refresh = typeof(MainWindow).GetMethod("RefreshTaskList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "RefreshTaskList");
        refresh.Invoke(main, null);
    }

    private static async Task<JobRowView> CaptureStageHover(MainWindow main, string fileName, string captureName)
    {
        var list = main.FindControl<ListBox>("JobList") ?? throw new InvalidOperationException("JobList not found");
        var job = list.Items.OfType<Job>().Single(item => Path.GetFileName(item.Inputs.FirstOrDefault() ?? "") == fileName);
        list.ScrollIntoView(job);
        await Settle(main);
        var row = main.GetVisualDescendants().OfType<JobRowView>().Single(item => ReferenceEquals(item.DataContext, job));
        var status = row.FindControl<Control>("StatusArea") ?? throw new InvalidOperationException("StatusArea not found");
        ToolTip.SetIsOpen(status, true);
        await Task.Delay(500);
        await CaptureWindowAndPopups(main, captureName);
        ToolTip.SetIsOpen(status, false);
        return row;
    }

    private static async Task CaptureWindowAndPopups(MainWindow window, string name, double extraWidth = 0)
    {
        window.Topmost = true; window.Activate();
        await Task.Delay(150);
        var scale = window.RenderScaling;
        var position = ((Window)window).PointToScreen(default);
        var size = window.ClientSize;
        var path = Path.Combine(_output, name + ".png");
        var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "-c", "from PIL import ImageGrab; import sys; x,y,w,h=map(int,sys.argv[1:5]); ImageGrab.grab(xdisplay=':11').crop((x,y,x+w,y+h)).save(sys.argv[5])",
            position.X.ToString(), position.Y.ToString(), ((int)Math.Round((size.Width + extraWidth) * scale)).ToString(), ((int)Math.Round(size.Height * scale)).ToString(), path })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        process.Dispose();
        window.Topmost = false;
        Captures.Add(new { file = name + ".png", theme = window.ActualThemeVariant.Key?.ToString(), width = size.Width, height = size.Height });
        Console.WriteLine("Captured " + name);
    }

    private static string JobName(JobRowView row)
        => row.DataContext is Job job ? Path.GetFileName(job.Inputs.FirstOrDefault() ?? "") : "";
}
