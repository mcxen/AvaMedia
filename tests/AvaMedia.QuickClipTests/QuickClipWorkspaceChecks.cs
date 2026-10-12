using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

internal static class QuickClipWorkspaceChecks
{
    public static void Run(IMediaEngine engine, string source, string pending, MediaInfo info, Action<bool, string> check)
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Motion.SetReducedMotion(true); AppLanguage.Apply("zh-CN"); Skin.Apply("Light");
        var uncheckedPath = Path.Combine(Path.GetDirectoryName(source)!, "未勾选.mp4");
        var editedPath = Path.Combine(Path.GetDirectoryName(source)!, "已有剪辑.mp4");
        var savedEdit = new ClipEditResult(editedPath, info, [new() { Start = .2, End = 1.2 }]);
        // The workspace is internal; exercise its real controls without adding a production test API.
        var workspaceType = typeof(EditorWindow).Assembly.GetType("AvaMedia.Desktop.QuickClipWorkspaceWindow")!;
        var workspace = (Window)Activator.CreateInstance(workspaceType, engine,
            new[] { source, pending, uncheckedPath, editedPath }, new[] { savedEdit })!;
        workspace.Show(); Pump();
        var files = workspace.GetVisualDescendants().OfType<ListBox>().Single();
        var sources = files.Items.Cast<object>().ToArray();
        ClipEditResult? Edit(int index) => (ClipEditResult?)sources[index].GetType().GetProperty("Edit")!.GetValue(sources[index]);
        workspace.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, Path.GetFileName(uncheckedPath))).IsChecked = false;
        Click(workspace.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "剪辑此视频…")));
        Until(() => workspace.OwnedWindows.OfType<EditorWindow>().Any());
        var editor = workspace.OwnedWindows.OfType<EditorWindow>().Single(); Ready(editor);
        editor.FindControl<TextBox>("StartTime")!.Text = "00:00:00.400";
        editor.FindControl<TextBox>("EndTime")!.Text = "00:00:01.600"; Pump();
        Click(editor.FindControl<Button>("ConfirmButton")!);
        var prompt = editor.OwnedWindows.Single();
        check(prompt.Title == "还有视频未剪辑" && editor.IsVisible && workspace.IsVisible && !editor.ProceedToExport,
            "Export with another checked unedited video opens a modal prompt and keeps the editor and workspace open.");
        var names = prompt.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
        check(names.Contains(Path.GetFileName(pending)) && !names.Contains(Path.GetFileName(source)) &&
            !names.Contains(Path.GetFileName(uncheckedPath)) && !names.Contains(Path.GetFileName(editedPath)),
            "The prompt lists only other checked unedited videos.");
        check(Edit(0) is null && Edit(1) is null && Edit(2) is null && ReferenceEquals(Edit(3), savedEdit),
            "Blocked export creates no edits and preserves existing saved edits.");
        var okay = prompt.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "好"));
        Click(okay);
        check(!editor.OwnedWindows.Any() && editor.IsVisible && editor.ReadDraft() is { Start: .4, End: 1.6 },
            "Dismissing the prompt leaves the current draft intact in the editor.");
        Click(editor.FindControl<Button>("ConfirmButton")!);
        check(editor.OwnedWindows.Count == 1 && Edit(1) is null, "Retrying export still blocks without generating a whole-video edit.");
        Click(editor.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "好")));
        Click(editor.FindControl<Button>("QuickNextVideoButton")!);
        Until(() => workspace.OwnedWindows.OfType<EditorWindow>().Any(window => !ReferenceEquals(window, editor)));
        var next = workspace.OwnedWindows.OfType<EditorWindow>().Single(); Ready(next);
        check(!editor.IsVisible && !editor.ProceedToExport && Edit(0)?.Segments.Single() is { Start: .4, End: 1.6 } && Edit(1) is null,
            "Next video still saves the current edit and advances without creating the pending edit.");
        Click(next.FindControl<Button>("ConfirmButton")!);
        Until(() => !workspace.IsVisible);
        check(next.ProceedToExport && !next.IsVisible && Edit(1) is not null && Edit(2) is null && ReferenceEquals(Edit(3), savedEdit),
            "Export succeeds once all other checked videos are edited, ignoring unchecked videos and preserving saved edits.");
    }

    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Ready(EditorWindow editor) { Until(() => editor.Ready.IsCompleted); editor.Ready.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition()) { Pump(); if (DateTime.UtcNow > deadline) throw new TimeoutException("Quick-clip workspace action did not complete."); Thread.Sleep(5); }
        Pump();
    }
}
