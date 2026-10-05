using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

var root = Path.GetFullPath("artifacts/editor-style-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
var source = Path.Combine(root, "sample.mp4");
var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("AVAMEDIA_FFMPEG") ?? "ffmpeg") { UseShellExecute = false, CreateNoWindow = true };
foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "3", "-c:v", "mpeg4", "-c:a", "aac", "-y", source }) start.ArgumentList.Add(argument);
using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception("Media fixture failed."); }
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true);
var checks = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
void Until(Func<bool> condition) { var timer = Stopwatch.StartNew(); while (!condition() && timer.ElapsedMilliseconds < 15000) { Pump(); Thread.Sleep(5); } if (!condition()) throw new Exception("UI wait timed out."); Pump(); }
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
