using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

var root = Path.GetFullPath("artifacts/editor-style-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
var source = Path.Combine(root, "sample.mp4");
var start = new ProcessStartInfo(MediaEngine.Resolve("", "ffmpeg")) { UseShellExecute = false, CreateNoWindow = true };
foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "3", "-c:v", "mpeg4", "-c:a", "aac", "-y", source }) start.ArgumentList.Add(argument);
using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception("Media fixture failed."); }
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true);
var checks = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
void Until(Func<bool> condition) { var timer = Stopwatch.StartNew(); while (!condition() && timer.ElapsedMilliseconds < 60000) { Pump(); Thread.Sleep(5); } if (!condition()) throw new Exception("UI wait timed out."); Pump(); }
void Complete(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
T Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;
void Click(Window window, string name) { Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
ConversionOptions Draft(EditorWindow window) => window.ReadDraft();
void Inside(Window window, Control control, string name) { var point = control.TranslatePoint(default, window)!.Value; Check(point.X >= 0 && point.Y >= 0 && point.X + control.Bounds.Width <= window.Bounds.Width + 1 && point.Y + control.Bounds.Height <= window.Bounds.Height + 1, name + " stays inside the window"); }

Check(EditorTime.Format(1.312684) == "00:00:01.313" && EditorTime.Format(1.353333) == "00:00:01.353", "Readouts consistently show three decimal places");
Check(EditorTime.Format(59.9999) == "00:01:00.000" && EditorTime.Format(25 * 3600 + 1.2) == "25:00:01.200", "Timecode rounding handles minute carries and total hours");
Check(EditorTime.TryRead("00:00:00.333", .333333, out var exact) && exact == .333333, "An unchanged display preserves the exact frame timestamp");
Check(EditorTime.TryRead("00:00:00.444444", .333333, out var edited) && edited == .444444, "Explicit high precision edits remain accepted");
Check(!EditorTime.TryRead("00:61:00.000", 0, out _) && !EditorTime.TryRead("00:00:NaN", 0, out _), "Invalid timecodes are rejected");

// Optional visual capture of every skin for design review (no assertions); set AVAMEDIA_EDITOR_SHOTS to a folder.
if (Environment.GetEnvironmentVariable("AVAMEDIA_EDITOR_SHOTS") is { Length: > 0 } shots)
{
    Directory.CreateDirectory(shots);
    foreach (var skin in Environment.GetEnvironmentVariable("AVAMEDIA_EDITOR_VIDEO") is { Length: > 0 } ? [] : new[] { "Light", "Dark", "MacOS9" })
    {
        Skin.Apply(skin);
        var window = new EditorWindow(new MediaEngine(new()), source, new() { Format = "mp4", Start = .333333, End = 1.733333 });
        window.Show(); Complete(window.Ready); window.Width = 1240; window.Height = 780; Pump();
        window.CaptureRenderedFrame()?.Save(Path.Combine(shots, $"impl-editor-{skin.ToLowerInvariant()}.png")); window.Close(); Pump();
    }
    // Quick-clip mode at the demo size, in Chinese, on a design-review video when one is provided
    // (AVAMEDIA_EDITOR_VIDEO); three segments, the second selected, start-handle callout shown.
    AppLanguage.Apply("zh-CN");
    var video = Environment.GetEnvironmentVariable("AVAMEDIA_EDITOR_VIDEO") is { Length: > 0 } custom && File.Exists(custom) ? custom : source;
    foreach (var skin in new[] { "Light", "Dark", "MacOS9" })
    {
        Skin.Apply(skin);
        var probing = new MediaEngine(new()).Probe(video, CancellationToken.None); Complete(probing); var probe = probing.Result;
        var scale = probe.Duration / 222;
        ConversionOptions Seg(double start, double end, double speed = 1) => new() { Format = "mp4", Start = start * scale, End = end * scale, Speed = speed };
        var quick = new EditorWindow(new MediaEngine(new()), video, Seg(17.8, 53.3), "quick-workflow",
            segments: [Seg(17.8, 53.3), Seg(71, 104.3, 1.5), Seg(137.6, 186.5)]);
        quick.SetWorkflowStep(2, 5);
        quick.Show(); Complete(quick.Ready); quick.Width = 1280; quick.Height = 800; Pump();
        quick.FindControl<ListBox>("SegmentList")!.SelectedIndex = 1;
        var until = DateTime.UtcNow.AddSeconds(8); while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(10); }
        quick.SeekForReview(84.36 * scale);
        until = DateTime.UtcNow.AddSeconds(3); while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(10); }
        quick.FindControl<AvaMedia.MediaUi.FilmstripTimeline>("Filmstrip")!.ShowHandleCallout(true); Pump();
        quick.CaptureRenderedFrame()?.Save(Path.Combine(shots, $"impl5-clip-{skin.ToLowerInvariant()}.png"));
        // Review-only presentation state; production detection and its handlers are unchanged.
        Find<ComboBox>(quick, "DirectionCombo").SelectedIndex = 1;
        quick.ReadClipEdit(); // Flush the debounced segment edit before capturing the shared footer.
        Find<TextBlock>(quick, "DirectionStatus").Text = "识别建议：顺时针 90°；当前片段：顺时针 90°\n依据：画面中的人脸和地平线朝向一致，多帧结果相同";
        Find<Button>(quick, "ApplyDirectionButton").IsEnabled = true;
        Find<TabControl>(quick, "EditTabs").SelectedItem = Find<TabItem>(quick, "DirectionTab"); Pump();
        if (skin == "Light")
        {
            quick.CaptureRenderedFrame()!.Save(Path.Combine(shots, "impl5-orient-light.png"));
            Find<TabControl>(quick, "EditTabs").SelectedIndex = 1; Pump();
            quick.CaptureRenderedFrame()!.Save(Path.Combine(shots, "impl5-crop-light.png"));
        }
        Find<TabControl>(quick, "EditTabs").SelectedIndex = 0; Pump();
        Click(quick, "QuickInfoButton");
        var flyout = (Flyout)Find<Button>(quick, "QuickInfoButton").Flyout!;
        var popup = TopLevel.GetTopLevel((Control)flyout.Content!)!;
        using (var editorFrame = quick.CaptureRenderedFrame()!)
        using (var popupFrame = popup.CaptureRenderedFrame()!)
        using (var composite = new RenderTargetBitmap(editorFrame.PixelSize, new Vector(96, 96)))
        {
            using (var drawing = composite.CreateDrawingContext())
            {
                drawing.DrawImage(editorFrame, new Rect(editorFrame.Size));
                var origin = quick.PointToClient(popup.PointToScreen(default));
                drawing.DrawImage(popupFrame, new Rect(origin, popupFrame.Size));
            }
            composite.Save(Path.Combine(shots, $"impl5-info-{skin.ToLowerInvariant()}.png"));
        }
        flyout.Hide(); Pump();
        Click(quick, "EditOptionsButton");
        var output = quick.OwnedWindows.OfType<OptionsWindow>().Single();
        until = DateTime.UtcNow.AddSeconds(3); while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(10); }
        output.CaptureRenderedFrame()!.Save(Path.Combine(shots, $"impl5-output-{skin.ToLowerInvariant()}.png"));
        output.Close(); Pump();
        quick.Close(); Pump();
    }
    return;
}

// Verify the quick editor's new locations before the classic-editor metric checks.
AppLanguage.Apply("zh-CN");
Skin.Apply("Light");
var quickLayout = new EditorWindow(new MediaEngine(new()), source, new() { Format = "mp4" }, "quick-workflow");
quickLayout.Show(); Complete(quickLayout.Ready); Pump();
var quickTabs = Find<TabControl>(quickLayout, "EditTabs");
Check(Find<StackPanel>(quickLayout, "ModeButtons").Children.OfType<Button>().Where(b => b.IsVisible)
    .Select(b => ((TabItem)b.Tag!).Header).SequenceEqual(new[] { "剪辑", "画面裁剪", "自动转正" }), "Quick mode switch contains exactly clip, crop and orientation");
foreach (var tab in quickTabs.Items.OfType<TabItem>().Where(t => t.IsVisible))
{
    quickTabs.SelectedItem = tab; Pump();
    var filmstrip = Find<AvaMedia.MediaUi.FilmstripTimeline>(quickLayout, "Filmstrip");
    Check(filmstrip.IsEffectivelyVisible && filmstrip.Bounds.Height > 0, tab.Header + ": filmstrip stays visible");
    Inside(quickLayout, filmstrip, tab.Header + ": filmstrip");
}
quickTabs.SelectedIndex = 1; Pump();
Check(Find<TextBox>(quickLayout, "CropWidth").IsEffectivelyVisible && Find<CheckBox>(quickLayout, "DelogoMode").IsEffectivelyVisible,
    "Crop coordinates and watermark selection are reachable in the crop sidebar");
quickTabs.SelectedItem = Find<TabItem>(quickLayout, "DirectionTab"); Pump();
Check(Find<CheckBox>(quickLayout, "MirrorCheck").IsEffectivelyVisible && Find<Button>(quickLayout, "DetectDirectionButton").IsEffectivelyVisible,
    "Orientation exposes mirror and detection alongside the timeline");
quickTabs.SelectedIndex = 0; Pump();
var infoButton = Find<Button>(quickLayout, "QuickInfoButton");
infoButton.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, infoButton, new Pointer(1, PointerType.Mouse, true), infoButton, default, 0, new PointerPointProperties(), KeyModifiers.None)); Pump();
Check(infoButton.Flyout!.IsOpen, "Media information opens on hover");
infoButton.Flyout.Hide(); Click(quickLayout, "QuickInfoButton");
Check(infoButton.Flyout.IsOpen, "Media information opens on click");
var infoContent = (Control)((Flyout)infoButton.Flyout).Content!;
var infoValues = infoContent.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("mono")).ToArray();
Check(infoValues.Length == 5 && infoValues[0].Text == Path.GetFileName(source) && infoValues[1].Text == EditorTime.Format(3) && infoValues[2].Text == "320 × 180"
    && infoValues[3].Text == "mpeg4" && infoValues[4].Text == "aac", "Info popover shows actual file, duration, dimensions and both codecs");
infoButton.Flyout.Hide(); Click(quickLayout, "EditOptionsButton");
var quickOutput = quickLayout.OwnedWindows.OfType<OptionsWindow>().Single(); Pump();
var outputText = quickOutput.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "QuickOutputAudioStatus");
Check(outputText.Text == Find<TextBlock>(quickLayout, "AudioStatus").Text && outputText.IsEffectivelyVisible,
    "Output dialog displays the editor audio state");
Find<TextBlock>(quickLayout, "AudioStatus").Text = "此文件不含音轨"; Pump();
Check(outputText.Text == "此文件不含音轨", "Output audio state remains bound to the original status");
Check(quickOutput.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "QuickOutputPreviewNote").Text == Find<TextBlock>(quickLayout, "PreviewNote").Text,
    "Output notes moved into the full output dialog");
var outputTabs = quickOutput.GetVisualDescendants().OfType<TabControl>().Single();
Check(outputTabs.Items.OfType<TabItem>().Select(t => t.Header).SequenceEqual(new[] { "视频", "音频", "字幕" }),
    "Output configuration retains every ClipEdit page");
quickOutput.Close(); quickLayout.Close(); Pump();

foreach (var skin in new[] { "Light", "Dark", "MacOS9" })
{
    Skin.Apply(skin);
    var editor = new EditorWindow(new MediaEngine(new()), source, new() { Format = "mp4", Start = .333333, End = 1.733333 });
    editor.Show(); Complete(editor.Ready);
    foreach (var size in new[] { (1400d, 980d, "normal"), (1000d, 730d, "minimum") })
    {
        editor.Width = size.Item1; editor.Height = size.Item2; Pump();
        var first = Find<TextBox>(editor, "StartTime"); var last = Find<TextBox>(editor, "EndTime");
        Check(first.Bounds.Size == last.Bounds.Size && first.Bounds.Width == 168 && first.Bounds.Height == 32, skin + " " + size.Item3 + ": time fields share the same metrics");
        Check(first.FontFamily.Name == Find<TextBlock>(editor, "CurrentTime").FontFamily.Name && first.FontSize == 14 && Find<TextBlock>(editor, "TotalTime").FontSize == 14, skin + " " + size.Item3 + ": time typography is shared");
        var cancel = Find<Button>(editor, "EditorCancelButton"); var confirm = Find<Button>(editor, "ConfirmButton");
        Check(cancel.Bounds.Size == confirm.Bounds.Size && cancel.Bounds.Width == 168 && cancel.Bounds.Height == 36, skin + " " + size.Item3 + ": footer actions have equal sizes");
        foreach (var name in new[] { "StartMinusButton", "StartPlusButton", "EndMinusButton", "EndPlusButton" })
            Check(Find<Button>(editor, name).Bounds.Size == new Size(32,32), skin + " " + size.Item3 + ": " + name + " follows the square step role");
        Check(Find<ComboBox>(editor, "PrecisionCombo").Bounds.Height == 32 && Find<ComboBox>(editor, "SpeedCombo").Bounds.Height == 32 && Find<ComboBox>(editor, "FadeInCombo").Bounds.Height == 32, skin + " " + size.Item3 + ": dropdowns align with inputs");
        foreach (var name in new[] { "BackwardButton", "PlayButton", "StopButton", "ForwardButton", "SoundButton" })
        {
            var button = Find<Button>(editor, name);
            Check(button.Bounds.Size == new Size(40,40) && button.Content is ActionIcon icon && icon.Width == 24 && icon.Height == 24 && !string.IsNullOrEmpty(AutomationProperties.GetName(button)), skin + " " + size.Item3 + ": " + name + " has consistent accessible vector content");
        }
        foreach (var name in new[] { "StartTime", "EndTime", "SelectionDuration", "PrecisionCombo", "SpeedCombo", "EditorCancelButton", "ConfirmButton" }) Inside(editor, Find<Control>(editor, name), skin + " " + size.Item3 + ": " + name);
        var duration = Find<TextBlock>(editor, "SelectionDuration");
        var durationLabel = ((Panel)duration.Parent!).Children.OfType<TextBlock>().First();
        Check(Math.Abs(duration.Bounds.Center.Y - durationLabel.Bounds.Center.Y) < 1, skin + " " + size.Item3 + ": duration and label share a vertical center");
        var current = Find<TextBlock>(editor, "CurrentTime"); var play = Find<Button>(editor, "PlayButton");
        Check(Math.Abs(current.TranslatePoint(new Rect(current.Bounds.Size).Center, editor)!.Value.Y - play.TranslatePoint(new Rect(play.Bounds.Size).Center, editor)!.Value.Y) < 1, skin + " " + size.Item3 + ": time readout aligns with transport controls");
        if (editor.FindControl<Button>("PlaySelectionButton") is { } selection)
        {
            var tabs = Find<TabControl>(editor, "EditTabs"); var point = selection.TranslatePoint(default, tabs)!.Value;
            Check(point.Y + selection.Bounds.Height <= tabs.Bounds.Height, skin + " " + size.Item3 + ": selection preview is fully visible inside its tab");
        }
        editor.CaptureRenderedFrame()!.Save(Path.Combine(root, skin + "-" + size.Item3 + ".png"));
    }
    var draft = Draft(editor);
    Check(draft.Start == .333333 && draft.End == 1.733333, skin + ": displaying rounded fields does not quantize submitted boundaries");
    Find<Slider>(editor,"SeekBar").Value = 1.353333; Pump();
    Check(Find<TextBlock>(editor,"CurrentTime").Text == "00:00:01.353" && Find<TextBlock>(editor,"TotalTime").Text == "00:00:03.000", skin + ": current and total time use the same format");
    Click(editor,"SoundButton"); Check(Find<ActionIcon>(editor,"SoundIcon").Kind == "muted", skin + ": mute keeps the vector control and updates its state");
    Click(editor,"SoundButton"); Check(Find<ActionIcon>(editor,"SoundIcon").Kind == "speaker", skin + ": unmute restores the same icon family");
    Click(editor,"PlayButton"); Until(() => Find<ActionIcon>(editor,"PlayIcon").Kind == "pause");
    Check(Find<Button>(editor,"PlayButton").Content is ActionIcon && AutomationProperties.GetName(Find<Button>(editor,"PlayButton")) == "暂停", skin + ": playback changes the vector glyph and accessible name");
    Click(editor,"PlayButton"); Until(() => Find<ActionIcon>(editor,"PlayIcon").Kind == "play");
    Find<TextBox>(editor,"StartTime").Text = "00:00:00.444444";
    Find<TextBox>(editor,"StartTime").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent)); Pump();
    Check(Draft(editor).Start == .444444 && Find<TextBox>(editor,"StartTime").Text == "00:00:00.444", skin + ": manual edits keep backend precision and normalize presentation");
    editor.Close(); Pump();
}
File.WriteAllText(Path.Combine(root,"report.json"), JsonSerializer.Serialize(new { checks = checks.Count, results = checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Verified {checks.Count} editor style checks. {root}");
