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
var splitDraft = new ConversionOptions { Start = .5, End = 3.5, Speed = 2, CropX = 80, CropY = 40, CropWidth = 64, CropHeight = 40 };
var timedParts = ClipSplit.Create(splitDraft, 4, new(ClipSplitMode.FixedDuration, SegmentSeconds: 1.2));
Check(timedParts.Count == 3 && timedParts[0].Start == .5 && timedParts[2].End == 3.5 && Math.Abs(timedParts[2].End - timedParts[2].Start - .6) < 1e-9, "Fixed-duration split lost its remainder.");
Check(timedParts.Zip(timedParts.Skip(1)).All(p => p.First.End == p.Second.Start) && timedParts.All(o => o.Speed == 2 && o.CropX == 80 && o.CropWidth == 64), "Fixed-duration boundaries are not contiguous or lost editing parameters.");
timedParts[0].CropX = 0; Check(timedParts[1].CropX == 80 && splitDraft.CropX == 80, "Split segments share their caller's mutable editing draft.");
Check(ClipSplit.Create(new() { End = .30000000000000004 }, 1, new(ClipSplitMode.FixedDuration, SegmentSeconds: .1)).Count == 3, "Floating point arithmetic produced a phantom tail segment.");
Check(ClipSplit.Create(new() { End = .3 }, 1, new(ClipSplitMode.FixedDuration, SegmentSeconds: .3 - 1e-12)).Count == 2, "A real short remainder was discarded next to a single full segment.");
Check(ClipSplit.ParseTime("25:01:02.125") == 90062.125 && ClipSplit.ParseTime("02:03.5") == 123.5 && ClipSplit.ParseTime(".125") == .125, "Split time parser lost hours or fractional seconds.");
var longTime = 90062.125125;
Check(MediaTime.Format(longTime).StartsWith("25:01:02.") && Math.Abs(ClipSplit.ParseTime(MediaTime.Format(longTime)) - longTime) < .000001, "Long video time display wraps after 24 hours or loses fractional boundaries.");
var shortTime = .000125;
Check(ClipSplit.ParseTime(MediaTime.Format(shortTime)) == shortTime, "Sub-millisecond split time is displayed as a zero boundary.");
var timeEntry = new QuickClipEntry(input, new() { Start = longTime, End = longTime + shortTime });
var conversionTimeEntry = new ConversionEntry(input, timeEntry.Options);
Check(timeEntry.Range.Contains(MediaTime.Format(longTime)) && timeEntry.Range.Contains(MediaTime.Format(longTime + shortTime)) && conversionTimeEntry.Summary.Contains(MediaTime.Format(longTime + shortTime)) && new ClipSegmentEntry(timeEntry.Options).Summary.Contains(MediaTime.Format(longTime + shortTime)), "Quick-clip, workflow segment and conversion row summaries disagree with the editing timeline display.");
Check(ClipSplit.ParsePoints("00:01，2.5; 00:03\n3.25").SequenceEqual([1d, 2.5, 3, 3.25]), "Split time-point separators do not support Chinese and multiline input.");
Reject(() => ClipSplit.ParseTime("00:60:00"), "Invalid colon minute accepted.");
Reject(() => ClipSplit.ParseTime("NaN"), "Non-finite split time accepted.");
Reject(() => ClipSplit.ParseTime("1:2.5:3"), "Fractional hour/minute field accepted.");
Reject(() => ClipSplit.Create(new() { End = 5 }, 4, new()), "Split silently clamps an interval beyond the source duration.");
Reject(() => ClipSplit.Create(new() { End = double.NaN }, 4, new()), "Non-finite split interval accepted.");
Reject(() => ClipSplit.Create(splitDraft, 4, new(ClipSplitMode.FixedDuration, SegmentSeconds: .01)), "Unbounded fixed-duration segment count accepted.");
Reject(() => ClipSplit.Create(splitDraft, 4, new(ClipSplitMode.FixedDuration, SegmentSeconds: 3)), "A single unsplit interval accepted as fixed-duration splitting.");
foreach (var cuts in new[] { new[] { .5, 1d }, new[] { 1d, 3.5 }, new[] { 2d, 1d }, new[] { 1d, 1d }, new[] { double.PositiveInfinity } })
    Reject(() => ClipSplit.Create(splitDraft, 4, new(ClipSplitMode.TimePoints, TimePoints: cuts)), "Invalid or unordered time points accepted: " + string.Join(",", cuts));
var pattern = Path.Combine(root, "分段画面.mkv");
Check(Run("-v", "error", "-n", "-f", "lavfi", "-i", "nullsrc=size=160x90:rate=25,geq=lum='20+floor(N/25)*50+floor(X/80)*10':cb=128:cr=128", "-t", "4", "-c:v", "ffv1", pattern).ExitCode == 0, "Split pixel fixture creation failed.");
var patternHash = SHA256.HashData(File.ReadAllBytes(pattern));
foreach (var segment in ClipSplit.Create(splitDraft, 4, new(ClipSplitMode.FixedDuration, SegmentSeconds: 1.2)))
{
    var splitJob = QuickClipBatch.CreateJobs([new(pattern, QuickClipBatch.ResolveOptions(pattern, "MP4", segment))], root, settingName: "定时分割").Single();
    engine.Execute(splitJob, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(splitJob.Output);
    var splitInfo = engine.Probe(splitJob.Output).GetAwaiter().GetResult();
    Check(splitInfo.Width == 64 && splitInfo.Height == 40 && Math.Abs(splitInfo.Duration - (segment.End - segment.Start) / 2) < .09, "Fixed-duration actual crop or speed is incorrect.");
    CheckFrame(splitJob, segment.Start, 0, "Fixed-duration output starts with the requested source/crop pixels.");
}
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
Click(editor.FindControl<Button>("ConfirmButton")!);
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
var savedClip=first.Options.Clone();var invalidCrop=savedClip.Clone();invalidCrop.CropX=280;invalidCrop.CropWidth=64;invalidCrop.CropHeight=40;first.SetOptions(invalidCrop);
var cropRejected=false;try{window.CreateRequest();}catch(ArgumentException ex){cropRejected=ex.Message.Contains("超出");}Check(cropRejected,"Quick clip submits a crop outside the selected source geometry.");first.SetOptions(savedClip);
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "⚙ 输出配置")));
settings=window.OwnedWindows.OfType<OptionsWindow>().Single();settings.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="VideoStreamIndex").Text="99";
Click(settings.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定")));Pump(window.Ready);
Check(window.Entries.All(e=>e.Info is null&&e.Error.Length>0),"Failed stream reload leaves stale source metadata.");
var streamRejected=false;try{window.CreateRequest();}catch(InvalidDataException){streamRejected=true;}Check(streamRejected,"Unavailable selected video stream can be submitted.");
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "⚙ 输出配置")));
settings=window.OwnedWindows.OfType<OptionsWindow>().Single();settings.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="VideoStreamIndex").Text="0";
Click(settings.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定")));Pump(window.Ready);
Check(window.Entries.All(e=>e.Info is not null&&e.Error.Length==0)&&window.CreateRequest().ClipInputs!.Count==2,"Corrected video stream does not restore valid quick clip drafts.");
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
// Exercise both new split modes through the real row dialog; cancellation and invalid points preserve the row.
Application.Current!.RequestedThemeVariant = ThemeVariant.Light; Dispatcher.UIThread.RunJobs();
var modesWindow = new QuickClipWindow(engine, root, [pattern, portrait]); modesWindow.Show(); Pump(modesWindow.Ready);
var modeEntry = modesWindow.Entries[0]; var untouchedEntry = modesWindow.Entries[1]; modeEntry.SetOptions(splitDraft);
modesWindow.FindControl<ComboBox>("FormatCombo")!.SelectedItem = "MP4";
Button SplitButton() => modesWindow.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "分割") && ReferenceEquals(b.DataContext, modeEntry));
Click(SplitButton()); var modeDialog = modesWindow.OwnedWindows.OfType<ClipSplitWindow>().Single();
modeDialog.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 2.5m;
Check(!modeDialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定")).IsEnabled, "Fractional equal-part count accepted.");
modeDialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 1;
var secondsInput = modeDialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SplitSeconds");
var splitOk = modeDialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定"));
Check(!splitOk.IsEnabled, "Fixed split allows the default duration longer than the selected source range.");
secondsInput.Text = "1.2"; Dispatcher.UIThread.RunJobs();
Check(splitOk.IsEnabled && modeDialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SplitPreview").Text!.Split(Environment.NewLine).Length == 3, "Fixed-duration preview did not update or the hidden part-count field still blocks it.");
Capture(modeDialog, "split-duration-light.png", 620, 620);
Click(modeDialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "取消")));
Check(modesWindow.Entries.Count == 2 && ReferenceEquals(modesWindow.Entries[0], modeEntry) && modeEntry.Options.Start == .5 && modeEntry.Options.End == 3.5, "Cancelling split modifies the source row.");
Click(SplitButton()); modeDialog = modesWindow.OwnedWindows.OfType<ClipSplitWindow>().Single();
modeDialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
var pointsInput = modeDialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SplitPoints");
splitOk = modeDialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定"));
pointsInput.Text = "1, 1"; Dispatcher.UIThread.RunJobs();
Check(!splitOk.IsEnabled && modeDialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SplitError").Text!.Contains("递增") && modesWindow.Entries.Count == 2, "Duplicate split points do not retain the row with an inline error.");
pointsInput.Text = "00:01\n00:02"; Dispatcher.UIThread.RunJobs();
Check(splitOk.IsEnabled, "Corrected split points cannot be accepted.");
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs(); Capture(modeDialog, "split-points-dark.png", 620, 620);
modeDialog.Width = 560; modeDialog.Height = 560; Capture(modeDialog, "split-points-minimum.png", 560, 560);
Check(splitOk.TranslatePoint(new Point(splitOk.Bounds.Width, splitOk.Bounds.Height), modeDialog) is { } corner && corner.X <= 540 && corner.Y <= 540 && modeDialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SplitPreview").Bounds.Height > 40, "Split controls or preview overflow at minimum window size.");
Click(splitOk); Pump(modesWindow.Ready);
Check(modesWindow.Entries.Count == 4 && ReferenceEquals(modesWindow.Entries[3], untouchedEntry), "Time-point split replaces the wrong source row.");
var selectedParts = modesWindow.Entries.Take(3).ToArray();
Check(selectedParts.Select(e => e.Options.Start).SequenceEqual([.5, 1, 2]) && selectedParts.Select(e => e.Options.End).SequenceEqual([1d, 2, 3.5]), "Time-point dialog lost the source timeline boundaries.");
Check(selectedParts.All(e => e.Options.Speed == 2 && e.Options.CropX == 80 && e.Options.CropWidth == 64), "Time-point split lost per-file crop or applied speed twice.");
var modeRequest = modesWindow.CreateRequest();
Pump(Task.Run(() =>
{
    foreach (var splitJob in QuickClipBatch.CreateJobs(modeRequest.ClipInputs!.Take(3), root, settingName: "时间点分割"))
    {
        engine.Execute(splitJob, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(splitJob.Output);
        var splitInfo = engine.Probe(splitJob.Output).GetAwaiter().GetResult();
        Check(splitInfo.Width == 64 && splitInfo.Height == 40 && Math.Abs(splitInfo.Duration - (splitJob.Options.End - splitJob.Options.Start) / 2) < .09, "Time-point UI output has incorrect crop or speed-adjusted duration.");
        CheckFrame(splitJob, splitJob.Options.Start, 0, "Time-point UI output starts in the correct source segment/crop.");
        CheckFrame(splitJob, splitJob.Options.End - .04, -.06, "Time-point output includes pixels from the next excluded interval.");
    }
}));
Check(SHA256.HashData(File.ReadAllBytes(pattern)).SequenceEqual(patternHash), "Splitting changes the source video bytes.");
modesWindow.Close();
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { checks, outputs, packetCopyVerified = true, uiActionsVerified = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} quick clip checks / {outputs.Count} actual outputs. {root}");

JsonDocument PacketHashes(string path) => JsonDocument.Parse(ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=data_hash", "-show_data_hash", "sha256", "-of", "json", path]).GetAwaiter().GetResult().Output);
void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { Check(true, message); return; } throw new Exception(message); }
void CheckFrame(Job job, double sourceTime, double outputTime, string message)
{
    var expected = Path.Combine(root, "expected-" + Guid.NewGuid() + ".gray"); var actual = Path.Combine(root, "actual-" + Guid.NewGuid() + ".gray");
    var o = job.Options;
    Check(Run("-v", "error", "-n", "-ss", sourceTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "-i", job.Inputs[0], "-frames:v", "1", "-vf", $"crop={o.CropWidth}:{o.CropHeight}:{o.CropX}:{o.CropY}:exact=1", "-pix_fmt", "gray", "-f", "rawvideo", expected).ExitCode == 0, "Expected split frame cannot be decoded.");
    Check(Run("-v", "error", "-n", outputTime < 0 ? "-sseof" : "-ss", outputTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "-i", job.Output, "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", actual).ExitCode == 0, "Output split frame cannot be decoded.");
    var a = File.ReadAllBytes(actual); var b = File.ReadAllBytes(expected);
    Check(a.Length == o.CropWidth * o.CropHeight && b.Length == a.Length && a.Zip(b).Average(p => Math.Abs(p.First - p.Second)) < 3, message);
}
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
