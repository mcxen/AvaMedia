using System.Security.Cryptography;
using System.Text.Json;
using AvaMedia.Core;
using AvaMedia.Desktop;

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
var parts = ClipSplit.Create(new() { Start = .5, End = 3.5, Speed = 2 }, 4, new(Parts: 3));
Check(parts.Count == 3 && parts[0].Start == .5 && parts[2].End == 3.5 && parts[0].End == parts[1].Start && parts[1].End == parts[2].Start && parts.All(o => o.Speed == 2), "Splits lost boundaries or filters.");
// Pixel comparisons need explicit quality: this synthetic lossless source has an unusually low bitrate.
var splitDraft = new ConversionOptions { Start = .5, End = 3.5, Speed = 2, CropX = 80, CropY = 40, CropWidth = 64, CropHeight = 40, VideoRateMode = VideoRateMode.Quality, Quality = 1 };
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
var timeOptions = new ConversionOptions { Start = longTime, End = longTime + shortTime };
var conversionTimeEntry = new ConversionEntry(input, timeOptions);
Check(conversionTimeEntry.Summary.Contains(MediaTime.Format(longTime + shortTime)) && new ClipSegmentEntry(timeOptions).Summary.Contains(MediaTime.Format(longTime + shortTime)), "Workflow segment and conversion row summaries disagree with the editing timeline display.");
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

Check(SHA256.HashData(File.ReadAllBytes(pattern)).SequenceEqual(patternHash), "Splitting changes the source video bytes.");
QuickClipWorkspaceChecks.Run(engine, input, portrait, info, Check);
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { checks, outputs, packetCopyVerified = true }, new JsonSerializerOptions { WriteIndented = true }));
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
