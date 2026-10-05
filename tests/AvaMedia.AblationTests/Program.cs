using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvaMedia.Core;

var root = Path.GetFullPath(args.Length == 0
    ? "artifacts/ablation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
    : args[0]);
Directory.CreateDirectory(root);
var engine = new MediaEngine(new());
var checks = new List<CheckResult>();
var runs = new List<RunResult>();
var comparisons = new List<Comparison>();
var sources = new Dictionary<string, string>();
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var status = "running";
string? failure = null;
string ffmpegVersion = "";

void Check(bool passed, string name)
{
    checks.Add(new(name, passed));
    if (!passed) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}

async Task<ProcessResult> FF(params string[] arguments)
{
    var result = await ProcessRunner.Run(engine.FFmpeg, arguments);
    if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
    return result;
}

void RememberSource(string path) => sources.Add(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

async Task<RunResult> Run(string family, string name, string input, ConversionOptions options)
{
    var output = Path.Combine(root, family + "-" + name + "." + options.Format);
    var job = new Job { FeatureId = family == "video" ? "mkv" : "audio-wav", Inputs = [input], Options = options, Output = output };
    var timer = Stopwatch.StartNew();
    await engine.Execute(job, _ => { }, CancellationToken.None);
    timer.Stop();
    var info = await engine.Probe(output);
    var result = new RunResult(family, name, Path.GetFileName(output), options.Clone(), info.Duration,
        info.Width, info.Height, info.AudioSampleRate, info.AudioChannels, new FileInfo(output).Length, timer.Elapsed.TotalMilliseconds);
    runs.Add(result);
    return result;
}

async Task<byte[]> Frame(RunResult run, double time)
{
    var file = Path.Combine(root, run.Family + "-" + run.Name + "-" + time.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ".rgb");
    await FF("-v", "error", "-n", "-ss", MediaEngine.Number(time), "-i", Path.Combine(root, run.Output),
        "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", file);
    var bytes = File.ReadAllBytes(file);
    Check(bytes.Length == run.Width * run.Height * 3, run.Name + " frame decodes completely at " + time);
    return bytes;
}

async Task<short[]> Samples(RunResult run)
{
    var file = Path.Combine(root, run.Family + "-" + run.Name + ".s16");
    await FF("-v", "error", "-n", "-i", Path.Combine(root, run.Output), "-map", "0:a:0",
        "-ar", "44100", "-ac", "1", "-c:a", "pcm_s16le", "-f", "s16le", file);
    var bytes = File.ReadAllBytes(file);
    return Enumerable.Range(0, bytes.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2))).ToArray();
}

double Mean(byte[] bytes) => bytes.Average(v => (double)v);
double Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs((double)x - y)).Average();
double Rms(short[] samples, double start, double end)
{
    var section = samples.Skip((int)(start * 44100)).Take((int)((end - start) * 44100)).ToArray();
    if (section.Length == 0) throw new InvalidDataException("No samples in measurement interval.");
    return Math.Sqrt(section.Average(v => (double)v * v));
}

void Compare(string family, string name, string metric, double full, double removed, bool passed, string expectation)
{
    comparisons.Add(new(family, name, metric, full, removed, expectation, passed));
    Check(passed, family + " / remove " + name + " / " + expectation);
}

try
{
    ffmpegVersion = (await FF("-version")).Output.Split('\n')[0].Trim();
    var video = Path.Combine(root, "fixture-video.mkv");
    await FF("-v", "error", "-n", "-f", "lavfi", "-i",
        "color=c=red:size=320x240:rate=30,drawbox=x=160:y=0:w=160:h=240:color=blue:t=fill,drawbox=x=90:y=45:w=30:h=20:color=white:t=fill,drawgrid=width=10:height=10:thickness=1:color=yellow@0.8",
        "-t", "4", "-c:v", "ffv1", video);
    var subtitle = Path.Combine(root, "fixture-subtitle.srt");
    File.WriteAllText(subtitle, "1\n00:00:00,500 --> 00:00:03,500\nABLATION\n", new UTF8Encoding(false));
    RememberSource(video); RememberSource(subtitle);
    var videoOptions = new ConversionOptions
    {
        Format = "mkv", VideoCodec = "ffv1", Mute = true, Start = .5, End = 3.5, Speed = 1.5,
        CropX = 80, CropY = 40, CropWidth = 160, CropHeight = 80, Width = 240, Rotation = 90, Flip = true,
        DelogoX = 90, DelogoY = 45, DelogoWidth = 30, DelogoHeight = 20,
        FadeIn = .5, FadeOut = .5, AudioFadeIn = 0, AudioFadeOut = 0,
        Subtitle = subtitle, SubtitleMode = SubtitleMode.BurnIn, SubtitleFontSize = 16, SubtitleMargin = 12
    };
    var fullVideo = await Run("video", "full", video, videoOptions);
    Check(fullVideo.Width == 120 && fullVideo.Height == 240 && Math.Abs(fullVideo.Duration - 2) < .08,
        "Full video pipeline has the expected geometry and duration");
    var fullFrame = await Frame(fullVideo, .8);
    var videoAblations = new (string Name, Action<ConversionOptions> Remove)[]
    {
        ("trim", o => { o.Start = 0; o.End = 0; }),
        ("crop", o => { o.CropX = o.CropY = o.CropWidth = o.CropHeight = 0; }),
        ("scale", o => { o.Width = o.Height = 0; }),
        ("rotation", o => o.Rotation = 0),
        ("flip", o => o.Flip = false),
        ("speed", o => o.Speed = 1),
        ("fade", o => { o.FadeIn = o.FadeOut = 0; }),
        ("blur", o => { o.DelogoX = o.DelogoY = o.DelogoWidth = o.DelogoHeight = 0; }),
        ("subtitle", o => { o.Subtitle = ""; o.SubtitleMode = SubtitleMode.None; })
    };
    foreach (var (name, remove) in videoAblations)
    {
        var options = videoOptions.Clone(); remove(options);
        var ablated = await Run("video", "without-" + name, video, options);
        switch (name)
        {
            case "trim":
                Compare("video", name, "durationSeconds", fullVideo.Duration, ablated.Duration,
                    Math.Abs(ablated.Duration - 4 / 1.5) < .08, "duration becomes 4 / 1.5 seconds"); break;
            case "speed":
                Compare("video", name, "durationSeconds", fullVideo.Duration, ablated.Duration,
                    Math.Abs(ablated.Duration - 3) < .08, "duration becomes 3 seconds"); break;
            case "crop":
                Compare("video", name, "widthPixels", fullVideo.Width, ablated.Width,
                    ablated.Width == 180 && ablated.Height == 240, "uncropped output becomes 180 x 240"); break;
            case "scale":
                Compare("video", name, "widthPixels", fullVideo.Width, ablated.Width,
                    ablated.Width == 80 && ablated.Height == 160, "unscaled output becomes 80 x 160"); break;
            case "rotation":
                Compare("video", name, "widthPixels", fullVideo.Width, ablated.Width,
                    ablated.Width == 240 && ablated.Height == 120, "unrotated output becomes 240 x 120"); break;
            case "fade":
                var earlyFull = Mean(await Frame(fullVideo, .1));
                var earlyPlain = Mean(await Frame(ablated, .1));
                Compare("video", name, "earlyMeanRgb", earlyFull, earlyPlain,
                    earlyFull < earlyPlain * .45, "removing fade restores early frame brightness");
                var lateFull = Mean(await Frame(fullVideo, 1.9));
                var latePlain = Mean(await Frame(ablated, 1.9));
                Compare("video", name, "lateMeanRgb", lateFull, latePlain,
                    lateFull < latePlain * .45, "removing fade restores late frame brightness"); break;
            default:
                var pixels = await Frame(ablated, .8);
                var difference = Difference(fullFrame, pixels);
                var minimum = name == "flip" ? 20 : .3;
                Compare("video", name, "meanAbsoluteRgbDifference", 0, difference,
                    fullFrame.Length == pixels.Length && difference > minimum,
                    "removing the stage changes decoded pixels"); break;
        }
    }

    // Fixed seed and a quiet interval distinguish denoising from general volume changes.
    var random = new Random(42);
    var pcm = new byte[4 * 44100 * 2];
    for (int i = 0; i < pcm.Length / 2; i++)
    {
        var t = i / 44100d;
        var tone = t is >= 1.5 and < 2.2 ? 0 : (t < 2 ? .15 : .35) * Math.Sin(2 * Math.PI * 440 * t);
        var noise = (random.NextDouble() * 2 - 1) * .003;
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)Math.Round((tone + noise) * 32767));
    }
    var raw = Path.Combine(root, "fixture-audio.s16"); File.WriteAllBytes(raw, pcm);
    var audio = Path.Combine(root, "fixture-audio.wav");
    await FF("-v", "error", "-n", "-f", "s16le", "-ar", "44100", "-ac", "1", "-i", raw, "-c:a", "pcm_s16le", audio);
    RememberSource(raw); RememberSource(audio);
    var audioOptions = new ConversionOptions
    {
        Format = "wav", Start = .5, End = 3.5, Speed = 1.5, Volume = .5,
        ReverseAudio = true, Echo = true, NoiseReduction = true, AudioFadeIn = .4, AudioFadeOut = .4,
        SampleRate = 32000, AudioChannels = 2
    };
    var fullAudio = await Run("audio", "full", audio, audioOptions);
    var fullSamples = await Samples(fullAudio);
    Check(Math.Abs(fullAudio.Duration - 2) < .04 && fullAudio.SampleRate == 32000 && fullAudio.Channels == 2,
        "Full audio pipeline has the expected duration, rate and channels");
    var audioAblations = new (string Name, Action<ConversionOptions> Remove)[]
    {
        ("trim", o => { o.Start = 0; o.End = 0; }),
        ("speed", o => o.Speed = 1),
        ("volume", o => o.Volume = 1),
        ("reverse", o => o.ReverseAudio = false),
        ("echo", o => o.Echo = false),
        ("denoise", o => o.NoiseReduction = false),
        ("fade", o => { o.AudioFadeIn = o.AudioFadeOut = 0; }),
        ("resample", o => o.SampleRate = 0),
        ("channels", o => o.AudioChannels = 0)
    };
    foreach (var (name, remove) in audioAblations)
    {
        var options = audioOptions.Clone(); remove(options);
        var ablated = await Run("audio", "without-" + name, audio, options);
        switch (name)
        {
            case "trim":
                Compare("audio", name, "durationSeconds", fullAudio.Duration, ablated.Duration,
                    Math.Abs(ablated.Duration - 4 / 1.5) < .05, "duration becomes 4 / 1.5 seconds"); break;
            case "speed":
                Compare("audio", name, "durationSeconds", fullAudio.Duration, ablated.Duration,
                    Math.Abs(ablated.Duration - 3) < .05, "duration becomes 3 seconds"); break;
            case "resample":
                Compare("audio", name, "sampleRateHz", fullAudio.SampleRate, ablated.SampleRate,
                    ablated.SampleRate == 44100, "source sample rate is retained"); break;
            case "channels":
                Compare("audio", name, "channels", fullAudio.Channels, ablated.Channels,
                    ablated.Channels == 1, "source channel count is retained"); break;
            default:
                var samples = await Samples(ablated);
                var delta = fullSamples.Zip(samples, (a, b) => Math.Abs((double)a - b)).Average();
                if (name == "volume")
                {
                    var level = Rms(fullSamples, .5, .75); var louder = Rms(samples, .5, .75);
                    Compare("audio", name, "toneRms", level, louder, louder / level is > 1.95 and < 2.05,
                        "removing 50% volume doubles the signal amplitude");
                }
                else if (name == "denoise")
                {
                    var level = Rms(fullSamples, 1.08, 1.23); var noisy = Rms(samples, 1.08, 1.23);
                    Compare("audio", name, "quietIntervalRms", level, noisy, noisy > level * 1.08,
                        "removing denoising increases noise energy in the quiet interval");
                }
                else if (name == "fade")
                {
                    var early = Rms(fullSamples, .05, .12); var noFade = Rms(samples, .05, .12);
                    Compare("audio", name, "earlyRms", early, noFade, early < noFade * .35,
                        "removing fade restores early sample amplitude");
                    var late = Rms(fullSamples, 1.85, 1.93); var noLateFade = Rms(samples, 1.85, 1.93);
                    Compare("audio", name, "lateRms", late, noLateFade, late < noLateFade * .4,
                        "removing fade restores late sample amplitude");
                }
                else
                {
                    Compare("audio", name, "meanAbsolutePcmDifference", 0, delta, delta > 100,
                        "removing the stage changes decoded PCM samples");
                }
                break;
        }
    }
    foreach (var (path, hash) in sources)
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash, "Source bytes unchanged: " + Path.GetFileName(path));
    Check(runs.Count == 20 && comparisons.Count == 20, "All 18 ablations and both fade endpoints completed");
    status = "passed";
}
catch (Exception ex)
{
    status = "failed"; failure = ex.ToString(); Environment.ExitCode = 1;
    Console.Error.WriteLine(ex.Message);
}
finally
{
    var revision = await ProcessRunner.Run("git", ["rev-parse", "HEAD"]);
    var workingTree = await ProcessRunner.Run("git", ["status", "--porcelain"]);
    File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new
    {
        schemaVersion = 1, status, failure, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        gitRevision = revision.Output.Trim(), workingTreeDirty = !string.IsNullOrWhiteSpace(workingTree.Output),
        ffmpegVersion, ablations = runs.Count - 2, checks = checks.Count, passedChecks = checks.Count(c => c.Passed),
        sources = sources.Select(s => new { file = Path.GetFileName(s.Key), sha256 = s.Value }), runs, comparisons, results = checks
    }, jsonOptions));
    Console.WriteLine($"{status}: {runs.Count} outputs / {checks.Count(c => c.Passed)} checks. {root}");
}

sealed record CheckResult(string Name, bool Passed);
sealed record RunResult(string Family, string Name, string Output, ConversionOptions Options, double Duration,
    int Width, int Height, int SampleRate, int Channels, long Bytes, double ElapsedMilliseconds);
sealed record Comparison(string Family, string RemovedStage, string Metric, double Full, double Ablated, string Expectation, bool Passed);
