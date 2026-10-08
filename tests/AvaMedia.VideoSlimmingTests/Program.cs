using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AvaMedia.Core;

if (args.Length is not (3 or 4)) throw new ArgumentException("Usage: <ffmpeg> <ffprobe> <empty-output-directory> [existing-fixture-directory]");
var folder = Path.GetFullPath(args[2]);
if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
    throw new IOException("Use an empty output directory to preserve previous evidence.");
Directory.CreateDirectory(folder);
var settings = new AppSettings { FFmpegPath = Path.GetFullPath(args[0]), FFprobePath = Path.GetFullPath(args[1]),
    OutputFolder = folder, MultiThread = true, CpuThreads = 4, AutoDetectGpu = false };
var engine = new MediaEngine(settings);
var slimmer = new VideoSlimming(engine);
var original = Path.Combine(folder, "01-low-quality-5MB.mp4");
var inflated = Path.Combine(folder, "02-inflated-120MB.mp4");
if (args.Length == 4)
{
    Console.WriteLine("Reusing the same generated source and inflated fixture after a code repair.");
    File.Copy(Path.Combine(args[3], Path.GetFileName(original)), original);
    File.Copy(Path.Combine(args[3], Path.GetFileName(inflated)), inflated);
}
else
{
    Console.WriteLine("Generating a 60-second blurry 720p video from a 320x180 source, approximately 5 MB.");
    await Ffmpeg(["-hide_banner", "-v", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=60",
    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=60", "-vf", "scale=1280:720:flags=bilinear,gblur=sigma=2",
    "-c:v", "libx264", "-preset", "medium", "-threads", "4", "-b:v", "600k", "-minrate", "600k", "-maxrate", "600k", "-bufsize", "1200k",
    "-x264-params", "nal-hrd=cbr:filler=1:force-cfr=1", "-c:a", "aac", "-b:a", "64k", "-t", "60", original]);
    Console.WriteLine("Inflating via 16 Mbps H.264 CBR re-encoding with filler, approximately 120 MB.");
    await Ffmpeg(["-hide_banner", "-v", "error", "-nostdin", "-n", "-i", original, "-c:v", "libx264", "-preset", "medium", "-threads", "4",
    "-b:v", "16000k", "-minrate", "16000k", "-maxrate", "16000k", "-bufsize", "32000k", "-x264-params", "nal-hrd=cbr:filler=1:force-cfr=1",
        "-c:a", "copy", "-fps_mode", "passthrough", inflated]);
}
var originalHash = await Hash(original); var inflatedHash = await Hash(inflated);
var inflatedQuality = await slimmer.MeasureAsync(original, inflated);
var options = new VideoSlimmingOptions { Preset = VideoSlimmingPreset.Balanced, Codec = "hevc", Format = "mkv" };
var jobs = VideoSlimming.CreateJobs([inflated], folder, options);
// Exercise the persisted request shape and actual production queue/executor, not a standalone ffmpeg shortcut.
var job = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(jobs[0]))!;
var queue = new QueueService(engine);
string last = "";
queue.Changed += changed =>
{
    if (changed.ProgressDetail == last) return;
    last = changed.ProgressDetail;
    Console.WriteLine($"{changed.Progress:0.#}% {changed.ProgressDetail}");
};
await queue.Run([job], 1);
if (job.State != JobState.Completed) throw new InvalidOperationException(job.Error + "\n" + job.Log);
var analysis = job.Options.VideoSlimming!.Analysis!;
var output = job.Output;
var outputQuality = await slimmer.MeasureAsync(inflated, output);
var originalQuality = await slimmer.MeasureAsync(original, output);
var before = await engine.Probe(original);
var bloated = await engine.Probe(inflated);
var after = await engine.Probe(output);
var originalFrames = await Frames(original); var inflatedFrames = await Frames(inflated); var outputFrames = await Frames(output);
var originalAudio = await AudioHash(original); var inflatedAudio = await AudioHash(inflated); var outputAudio = await AudioHash(output);
var originalBytes = new FileInfo(original).Length; var inflatedBytes = new FileInfo(inflated).Length; var outputBytes = new FileInfo(output).Length;
var checks = new Dictionary<string, bool>
{
    ["originalApproximately5MB"] = originalBytes is > 4500000 and < 5500000,
    ["inflatedApproximately120MB"] = inflatedBytes is > 110000000 and < 130000000,
    ["inflationPreservesPicture"] = inflatedQuality.Ssim >= .99,
    ["queueCompleted"] = job.State == JobState.Completed && job.Progress == 100,
    ["savingAtLeast90Percent"] = outputBytes < inflatedBytes * .1,
    ["backToOriginalSizeRange"] = outputBytes <= originalBytes * 1.5,
    ["wholeVideoMeetsBalancedSsim"] = outputQuality.Ssim >= .99,
    ["wholeVideoMeetsBalancedXpsnr"] = outputQuality.Xpsnr >= 38,
    ["pictureVsOriginal"] = originalQuality.Ssim >= .98,
    ["resolutionPreserved"] = before.Width == after.Width && before.Height == after.Height,
    ["frameRatePreserved"] = Math.Abs(before.FrameRate - after.FrameRate) < .001,
    ["allFramesPreserved"] = originalFrames == inflatedFrames && inflatedFrames == outputFrames && outputFrames == 1440,
    ["durationPreserved"] = Math.Abs(before.Duration - after.Duration) < .1,
    ["audioBitstreamPreserved"] = originalAudio == inflatedAudio && inflatedAudio == outputAudio,
    ["sourcesUnchanged"] = originalHash == await Hash(original) && inflatedHash == await Hash(inflated)
};
var report = new
{
    scenario = "Synthetic blurry 320x180 content upscaled to 720p, then high-CBR H.264 re-encoded with filler. This is deliberately inflated, not representative of every large video.",
    pipeline = "VideoSlimming.CreateJobs -> JSON roundtrip -> QueueService -> MediaEngine.Execute -> VideoSlimming.ExecuteAsync",
    source = new { path = original, bytes = originalBytes, sha256 = originalHash, media = before, frames = originalFrames, audioSha256 = originalAudio },
    inflated = new { path = inflated, bytes = inflatedBytes, sha256 = inflatedHash, media = bloated, frames = inflatedFrames, qualityVsOriginal = inflatedQuality },
    output = new { path = output, bytes = outputBytes, sha256 = await Hash(output), media = after, frames = outputFrames,
        qualityVsInflated = outputQuality, qualityVsOriginal = originalQuality, audioSha256 = outputAudio },
    analysis, savedPercent = (1 - outputBytes / (double)inflatedBytes) * 100,
    checks, passed = checks.Values.All(value => value), log = job.Log
};
await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{originalBytes / 1000000d:0.##} MB -> {inflatedBytes / 1000000d:0.##} MB -> {outputBytes / 1000000d:0.##} MB");
Console.WriteLine($"Whole-video SSIM {outputQuality.Ssim:0.######}; XPSNR {outputQuality.Xpsnr:0.##}; {outputFrames} frames; audio identical.");
if (checks.Any(check => !check.Value)) throw new InvalidOperationException(string.Join(", ", checks.Where(check => !check.Value).Select(check => check.Key)));
Console.WriteLine("PASS: requested end-to-end scenario.");

async Task<ProcessResult> Ffmpeg(IEnumerable<string> arguments)
{
    var result = await ProcessRunner.Run(engine.FFmpeg, arguments);
    if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
    return result;
}
async Task<long> Frames(string path)
{
    var result = await ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-select_streams", "v:0", "-count_frames",
        "-show_entries", "stream=nb_read_frames", "-of", "default=noprint_wrappers=1:nokey=1", path]);
    if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
    return long.Parse(result.Output.Trim(), CultureInfo.InvariantCulture);
}
async Task<string> AudioHash(string path) => (await Ffmpeg(["-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-"])).Output.Trim();
async Task<string> Hash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
}
