using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

var root = Path.GetFullPath("artifacts/quick-clip-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
var engine = new MediaEngine(new()); var checks = 0; var outputs = new List<string>();
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
ProcessResult Run(params string[] args) => ProcessRunner.Run(engine.FFmpeg, args).GetAwaiter().GetResult();
var input = Path.Combine(root, "样例 O'Brien space.mp4"); var portrait = Path.Combine(root, "竖屏.mp4");
Check(Run("-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-t", "4", "-c:v", "mpeg4", "-g", "12", "-q:v", "3", "-c:a", "aac", input).ExitCode == 0, "Fixture creation failed.");
Check(Run("-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=180x320:rate=25", "-t", "3", "-c:v", "mpeg4", "-q:v", "3", portrait).ExitCode == 0, "Portrait fixture creation failed.");
var originalHash = SHA256.HashData(File.ReadAllBytes(input));
var info = engine.Probe(input).GetAwaiter().GetResult();
foreach (var ext in new[] { "mp4", "mov", "mkv", "m4v" })
{
    var source = ext == "mp4" ? input : Path.Combine(root, "container." + ext);
    if (ext != "mp4")
    {
        var remuxArgs = new List<string> { "-v", "error", "-n", "-i", input, "-map", "0", "-c", "copy" };
        if (ext == "m4v") remuxArgs.AddRange(["-f", "mp4"]); remuxArgs.Add(source);
        Check(ProcessRunner.Run(engine.FFmpeg, remuxArgs).GetAwaiter().GetResult().ExitCode == 0, "Remux fixture failed: " + ext);
    }
    var draft = new ConversionOptions { Start = .6, End = 2.6 };
    var options = QuickClipBatch.ResolveOptions(source, "Fast Copy", draft);
    Check(options.Format == ext && options.CopyStreams && !draft.CopyStreams, "Fast Copy resolution mutated the draft or lost the container.");
    var job = QuickClipBatch.CreateJobs([new(source, options)], root, settingName: "FastCopy").Single();
    engine.Execute(job, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(job.Output);
    var result = engine.Probe(job.Output).GetAwaiter().GetResult();
    Check(result.VideoCodec == info.VideoCodec && result.AudioCodec == info.AudioCodec && result.Width == info.Width && result.Duration > 1.8 && result.Duration < 3, "Fast Copy output invalid: " + ext);
    using var sourcePackets = PacketHashes(source); using var copiedPackets = PacketHashes(job.Output);
    var hashes = sourcePackets.RootElement.GetProperty("packets").EnumerateArray().Select(p => p.GetProperty("data_hash").GetString()).ToHashSet();
    var copied = copiedPackets.RootElement.GetProperty("packets").EnumerateArray().ToArray();
    Check(copied.Length > 20 && copied.All(p => hashes.Contains(p.GetProperty("data_hash").GetString())), "Video packets were re-encoded: " + ext);
    using var streams = JsonDocument.Parse(ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-show_streams", "-of", "json", job.Output]).GetAwaiter().GetResult().Output);
    Check(streams.RootElement.GetProperty("streams").EnumerateArray().Count(s => s.GetProperty("codec_type").GetString() == "audio") == 1, "Fast Copy should select the first audio track.");
}
var fixedNames = QuickClipBatch.CreateJobs([new(input, new()), new(input, new())], root, reserved: [Path.Combine(root, "样例 O'Brien space (1).mp4")]);
Check(fixedNames[0].Output.EndsWith(" (2).mp4") && fixedNames[1].Output.EndsWith(" (3).mp4"), "Queued name collisions were not avoided.");
var perSource = QuickClipBatch.CreateJobs([new(input, new()), new(portrait, new())], "ignored", true);
Check(perSource.All(j => Path.GetDirectoryName(j.Output) == root && !j.Inputs.Contains(j.Output)), "Source directory outputs can overwrite inputs.");
var parts = QuickClipBatch.Split(new() { Start = .5, End = 3.5, Speed = 2 }, 4, 3);
Check(parts.Count == 3 && parts[0].Start == .5 && parts[2].End == 3.5 && parts[0].End == parts[1].Start && parts[1].End == parts[2].Start && parts.All(o => o.Speed == 2), "Splits lost boundaries or filters.");
var filtered = QuickClipBatch.ResolveOptions(input, "MKV", new() { Start = .5, End = 2.5, CropWidth = 160, CropHeight = 90 });
Check(!filtered.CopyStreams && filtered.Format == "mkv", "MKV preset is not re-encoding.");
var filteredJob = QuickClipBatch.CreateJobs([new(input, filtered)], root, settingName: "MKV").Single();
engine.Execute(filteredJob, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(filteredJob.Output);
var filteredInfo = engine.Probe(filteredJob.Output).GetAwaiter().GetResult();
Check(filteredInfo.Width == 160 && filteredInfo.Height == 90 && Math.Abs(filteredInfo.Duration - 2) < .15, "Re-encoded trim/crop output incorrect.");
var rejected = false;
try { QuickClipBatch.CreateJobs([new(input, QuickClipBatch.ResolveOptions(input, "Fast Copy", filtered))], root); } catch (ArgumentException) { rejected = true; }
Check(rejected, "Fast Copy accepted filters.");
Check(SHA256.HashData(File.ReadAllBytes(input)).SequenceEqual(originalHash), "Source media changed.");

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true);
var window = new QuickClipWindow(engine, root, [input, portrait]); window.Show(); Pump(window.Ready);
Check(window.Entries.All(e => e.Thumbnail is not null && e.Info is not null), "Row thumbnails did not load.");
Check(window.Preset == "Fast Copy" && window.FindControl<ComboBox>("FormatCombo")!.Items.Cast<string>().SequenceEqual(["Fast Copy", "MP4", "MKV"]), "Quick Clip format choices differ from reference.");
var first = window.Entries[0]; var second = window.Entries[1];
Click(window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "下移" && ReferenceEquals(b.DataContext, first)));
Check(window.Entries[1] == first && first.CanMoveUp && !first.CanMoveDown && !second.CanMoveUp, "Row down button/boundary states failed.");
Click(window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "上移" && ReferenceEquals(b.DataContext, first)));
Check(window.Entries[0] == first, "Row up button failed.");
Check(window.AddFiles([input]).Count == 0 && window.Entries.Count == 2, "Duplicate imports should not duplicate rows.");
var text = Path.Combine(root, "not-video.txt"); File.WriteAllText(text, "test");
Check(window.AddFiles([text]).Count == 1 && window.Entries.Count == 2, "Non-video imports were not filtered.");
Capture(window, "quick-clip-light.png");
Check(window.GetVisualDescendants().OfType<ActionIcon>().All(icon => icon.Bounds.Width > 0 && icon.Bounds.Height > 0), "Row vector icons have no visible bounds.");
Check(window.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "选项")).All(b => b.TranslatePoint(new Point(b.Bounds.Width, 0), window) is { } p && p.X <= window.Bounds.Width - 16), "File rows overflow beyond the viewport.");
// Enter the real editor from this row and accept only its timeline draft.
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "选项") && ReferenceEquals(b.DataContext, first)));
var editor = window.OwnedWindows.OfType<EditorWindow>().Single(); Pump(editor.Ready);
editor.FindControl<TextBox>("StartTime")!.Text = "00:00:00.500";
editor.FindControl<TextBox>("EndTime")!.Text = "00:00:02.500";
Click(editor.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "✓ 确定")));
PumpUntil(() => !editor.IsVisible); Pump(window.Ready);
Check(first.Options.Start == .5 && first.Options.End == 2.5 && second.Options.Start == 0 && second.Options.End == 0 && window.Preset == "Fast Copy", "Editing one file affected another or changed Fast Copy.");
window.FindControl<ComboBox>("OutputCombo")!.SelectedItem = QuickClipWindow.SourceDirectory;
window.FindControl<CheckBox>("AppendSetting")!.IsChecked = true;
var request = window.CreateRequest();
Check(request.OutputToSource && request.SettingName == "FastCopy" && request.ClipInputs![0].Options.Start == .5 && request.ClipInputs[1].Options.Start == 0, "Accepted request lost per-file settings/output controls.");
request.ClipInputs![0].Options.Start = 0;
Check(first.Options.Start == .5, "Submitted job options are not independent drafts.");
// A cancelled output-settings draft must leave timeline/format intact.
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "⚙ 输出配置")));
var settings = window.OwnedWindows.OfType<OptionsWindow>().Single();
Click(settings.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "取消")));
Check(window.Preset == "Fast Copy" && first.Options.Start == .5, "Cancelling settings changed the clip draft.");
// Shared output settings select encoding without losing individual timeline edits.
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "⚙ 输出配置")));
settings = window.OwnedWindows.OfType<OptionsWindow>().Single();
var widthRow = (Grid)settings.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "画面宽度 (0 = 原始)").Parent!;
widthRow.Children.OfType<TextBox>().Single().Text = "160";
Click(settings.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定")));
Check(window.Preset == "MP4" && first.Options.Start == .5 && first.Options.End == 2.5 && second.Options.Start == 0 && first.Options.Width == 160 && second.Options.Width == 160, "Shared output settings lost timeline drafts or failed to select encoding.");
Check(window.CreateRequest().ClipInputs!.All(i => !i.Options.CopyStreams), "Filtered requests still use stream copy.");
// The equal-parts dialog is exercised through its buttons.
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "分割") && ReferenceEquals(b.DataContext, first)));
var splitter = window.OwnedWindows.Single(); splitter.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 2;
Click(splitter.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定"))); Pump(window.Ready);
Check(window.Entries.Count == 3 && window.Entries[0].Options.End == window.Entries[1].Options.Start && window.Entries[2] == second, "Split dialog did not replace only the selected row.");
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "按名称排序")));
Check(window.Entries.Select(e => e.Name).SequenceEqual(window.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), "Sort button failed.");
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs(); Capture(window, "quick-clip-dark.png");
window.Width = 820; window.Height = 480; Capture(window, "quick-clip-minimum.png", 820, 480);
Check(window.FindControl<Button>("OkButton")!.Bounds.Width > 0 && window.FindControl<ComboBox>("OutputCombo")!.Bounds.Width > 0, "Minimum window size hides submit/output controls.");
Check(window.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "选项")).All(b => b.TranslatePoint(new Point(b.Bounds.Width, 0), window) is { } p && p.X <= 804), "File actions overflow at minimum window size.");
Click(window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "移除此条目"));
Check(window.Entries.Count == 2, "Row remove button failed.");
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "清空列表")));
Check(window.Entries.Count == 0 && window.FindControl<TextBlock>("EmptyText")!.IsVisible, "Clear list button failed.");
window.Close();
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { checks, outputs, packetCopyVerified = true, uiActionsVerified = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} quick clip checks / {outputs.Count} actual outputs. {root}");

JsonDocument PacketHashes(string path) => JsonDocument.Parse(ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=data_hash", "-show_data_hash", "sha256", "-of", "json", path]).GetAwaiter().GetResult().Output);
void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
void PumpUntil(Func<bool> done)
{
    var timeout = DateTime.UtcNow.AddSeconds(20);
    while (!done()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > timeout) throw new TimeoutException("UI action did not complete."); Thread.Sleep(5); }
    Dispatcher.UIThread.RunJobs();
}
void Capture(Window target, string name, int width = 1000, int height = 700)
{
    target.Measure(new Size(width, height)); target.Arrange(new Rect(0, 0, width, height)); Dispatcher.UIThread.RunJobs();
    using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96)); bitmap.Render(target); bitmap.Save(Path.Combine(root, name));
}
