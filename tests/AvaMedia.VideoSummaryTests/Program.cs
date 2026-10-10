using System.Text.Json;
using AvaMedia.Core;

// Usage: dotnet run -- [mock|live] [workdir]
//   mock (default): refusal, summary-refusal and minor-safety abort paths with mocked ISummaryModel / tagger.
//   live: real JoyTag + Ollama vision/summary on a short synthetic video (needs `ollama serve` and JoyTag installed).
var mode = args.ElementAtOrDefault(0) ?? "mock";
var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/video-summary-tests");
Directory.CreateDirectory(root);
var ffmpeg = File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : "ffmpeg";
var ffprobe = File.Exists("/usr/bin/ffprobe") ? "/usr/bin/ffprobe" : "ffprobe";

static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

AppSettings Settings(string vision, string text)
{
    var provider = new OnlineAiOptions { Name = "Ollama", Preset = "ollama", Endpoint = "http://localhost:11434/v1", TextModel = text, VisionModel = vision,
        TokenLimit = OnlineAiTokenLimit.MaxTokens, ResponseFormat = OnlineAiResponseFormat.Prompt, TimeoutSeconds = 300 };
    var settings = new AppSettings { FFmpegPath = ffmpeg, FFprobePath = ffprobe, OnlineAi = new OnlineAiSettings { Providers = [provider] } };
    settings.OnlineAi.DefaultProviderId = provider.Id;
    return settings;
}

async Task<string> SyntheticVideoAsync()
{
    var path = Path.Combine(root, "synthetic.mp4");
    if (File.Exists(path)) return path;
    var frames = Path.Combine(root, "frames-src"); Directory.CreateDirectory(frames);
    // Three distinct still scenes (red room / blue sky / green field), 2 s each.
    var colors = new[] { "0xB03030", "0x3060C0", "0x30A040" };
    for (var i = 0; i < colors.Length; i++)
    {
        var result = await ProcessRunner.Run(ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", $"color=c={colors[i]}:s=640x360:d=1",
            "-vf", $"drawbox=x={80 + i * 120}:y=100:w=160:h=160:color=white@0.9:t=fill", "-frames:v", "1", Path.Combine(frames, $"scene{i}.png")]);
        if (result.ExitCode != 0) throw new Exception(result.Error);
    }
    var make = await ProcessRunner.Run(ffmpeg, ["-v", "error", "-y", "-framerate", "0.5", "-i", Path.Combine(frames, "scene%d.png"),
        "-vf", "fps=10,format=yuv420p", "-c:v", "libx264", "-t", "6", path]);
    if (make.ExitCode != 0) throw new Exception(make.Error);
    return path;
}

Job SummaryJob(string video, string output, VideoSummaryOptions options) => new()
{
    FeatureId = "video-summary", Inputs = [video], Output = output, Options = new ConversionOptions { Format = "", VideoSummary = options }
};

VideoSummaryOptions Options(int frames = 3) => new()
{
    Provider = VideoSummaryProvider.Online, ExtractSubtitles = false, AnalyzeContent = false, ExtractAbstract = true, SummarizeContent = false,
    AnalyzeFrames = true, FrameCount = frames, TranscriptSource = VideoTranscriptSource.Automatic
};

if (mode == "mock")
{
    // 1. Guard unit checks.
    var t = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };
    IReadOnlyDictionary<string, float> Safe() => new Dictionary<string, float> { ["child"] = .02f, ["oppai_loli"] = .01f };
    Check(!MinorSafetyGuard.Evaluate(t, [Safe(), Safe()]).Blocked, "guard passes low scores");
    var blocked = MinorSafetyGuard.Evaluate(t, [Safe(), new Dictionary<string, float> { ["child"] = .02f, ["kindergarten_uniform"] = .55f }]);
    Check(blocked.Blocked && blocked.Reason!.Contains("kindergarten_uniform") && blocked.Hits.Count == 1, "guard blocks one frame above threshold");
    Check(MinorSafetyGuard.Evaluate(t, [Safe(), new Dictionary<string, float> { ["child"] = (float)MinorSafetyGuard.Threshold }]).Blocked, "threshold is inclusive");
    Check(Throws<InvalidDataException>(() => MinorSafetyGuard.Evaluate(t, [Safe()])), "count mismatch fails closed");
    Check(Throws<InvalidDataException>(() => MinorSafetyGuard.Evaluate(t, [Safe(), new Dictionary<string, float> { ["1girl"] = .9f }])), "missing safety vocabulary fails closed");
    Check(Throws<InvalidDataException>(() => MinorSafetyGuard.Evaluate([], [])), "no frames fails closed");
    var vocabulary = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "../../../../../src/AvaMedia.Core/Assets/AiLexicons/joytag.txt")).ToHashSet();
    Check(MinorSafetyGuard.Tags.Count(vocabulary.Contains) >= 6, "safety tags present in JoyTag vocabulary: " + string.Join(",", MinorSafetyGuard.Tags.Where(vocabulary.Contains)));

    // 2. Pipeline abort: neither model source may be opened.
    var opened = 0;
    var frames = Enumerable.Range(0, 3).Select(i => new VideoSummaryFrame(TimeSpan.FromSeconds(i), [1, 2, 3])).ToArray();
    VideoSummaryModelSource Source(string name, ISummaryModel model) => new(name, _ => { opened++; return Task.FromResult(model); });
    var abort = await VideoSummaryPipeline.RunAsync(frames, new MockTagger(index => index == 2 ? .9f : 0f),
        Source("v", new MockModel(_ => "x")), Source("s", new MockModel(_ => "x")), CancellationToken.None);
    Check(abort.Aborted && abort.AbortReason!.Length > 0 && abort.Frames.Count == 0 && abort.Summary is null && opened == 0, "pipeline abort opens no model and returns no frames");

    // 3. Frame refusal kept, summary notes it.
    var vision = new MockModel(call => call == 2 ? "Sorry, but I cannot describe this image." : $"一名成年人站在房间里（帧{call}）。");
    var text = new MockModel(_ => "视频展示了成年人在房间内的活动。");
    var ok = await VideoSummaryPipeline.RunAsync(frames, new MockTagger(_ => .01f), Source("vision-model", vision) with { ProviderId = "p-vision", ModelId = "vision-id" },
        Source("summary-model", text) with { ProviderId = "p-summary", ModelId = "summary-id" }, CancellationToken.None);
    Check(!ok.Aborted && ok.Frames.Count == 3 && ok.Frames[1].Refused && ok.Frames[1].Description is null && !ok.Frames[0].Refused, "refused frame kept with Refused=true");
    Check(ok.Frames.All(frame => frame.Model == "vision-model") && ok.SummaryModel == "summary-model" && ok.Summary is { Length: > 0 }, "models recorded separately");
    Check(ok.Frames.All(frame => frame is { ProviderId: "p-vision", ModelId: "vision-id", FramePath: null }) &&
        ok is { SummaryProviderId: "p-summary", SummaryModelId: "summary-id" }, "pipeline records provider/model ids; no frame path without saved image");
    Check(text.Prompts.Single().Contains("拒绝描述") && text.Prompts.Single().Contains("00:01"), "summary prompt notes refused frames");
    Check(vision.Systems.All(system => system.Contains("成人/NSFW")) && text.Systems.Single().Contains("成人/NSFW"), "adult-allowed prompts");

    // 4. Summary refusal surfaced.
    var refusing = await VideoSummaryPipeline.RunAsync(frames, new MockTagger(_ => .01f), Source("v", new MockModel(_ => "成年人。")),
        Source("s", new MockModel(_ => "抱歉，我无法协助处理这个请求。")), CancellationToken.None);
    Check(refusing.SummaryRefused && refusing.Summary is null && !refusing.Aborted && refusing.Frames.Count == 3, "summary refusal surfaced");

    // 5. Job abort path: no model, no frames or descriptions on disk, only report.json with abort record.
    var video = await SyntheticVideoAsync();
    var engine = new MediaEngine(Settings("moondream", "moondream"));
    var output = Path.Combine(root, "abort-" + Guid.NewGuid().ToString("N")[..8]);
    var factoryCalls = 0; var abortTagger = new MockTagger(index => index == 1 ? .8f : 0f);
    var service = new VideoSummaryService(engine, tagger: abortTagger, modelFactory: (_, _) => { factoryCalls++; return Task.FromResult<ISummaryModel>(new MockModel(_ => "x")); });
    VideoSummaryAbortedException? aborted = null;
    try { await service.ExecuteAsync(SummaryJob(video, output, Options()), _ => { }, CancellationToken.None); }
    catch (VideoSummaryAbortedException error) { aborted = error; }
    Check(aborted is { Result.Aborted: true } && aborted.Result.Frames.Count == 0 && aborted.Result.Summary is null, "job aborts with VideoSummaryAbortedException");
    Check(factoryCalls == 0, "job abort calls no description/summary model");
    Check(abortTagger.Seen >= VideoSummaryService.SafetyFrames, $"safety checks sampled + uniform frames ({abortTagger.Seen})");
    var files = Directory.GetFileSystemEntries(output).Select(Path.GetFileName).ToArray();
    Check(files.SequenceEqual(["report.json"]), "aborted output contains only report.json: " + string.Join(",", files));
    var loaded = VideoSummaryService.LoadResult(output);
    Check(loaded is { Aborted: true, Frames.Count: 0, Summary: null } && loaded.AbortReason == aborted!.Result.AbortReason, "report.json carries abort fields");
    Check(!Directory.EnumerateDirectories(root, ".AvaMedia-summary-*").Any(), "no staging left behind");

    // 6. Job success path with a refused frame and mocked models.
    output = Path.Combine(root, "ok-" + Guid.NewGuid().ToString("N")[..8]);
    var jobVision = new MockModel(call => call == 1 ? "I'm unable to help with that." : "画面中有彩色背景和一个白色方块。");
    var jobText = new MockModel(_ => "视频由三个纯色场景组成，每个场景有一个白色方块。");
    var okService = new VideoSummaryService(engine, tagger: new MockTagger(_ => .01f),
        modelFactory: (id, _) => Task.FromResult<ISummaryModel>(id == ModelCatalog.SummaryVisionId ? jobVision : jobText));
    var result = await okService.ExecuteAsync(SummaryJob(video, output, Options()), _ => { }, CancellationToken.None);
    Check(!result.Aborted && result.Frames.Count >= 2 && result.Frames.Count(frame => frame.Refused) == 1 && result.Summary is { Length: > 0 }, "job result has refused frame and summary");
    var report = VideoSummaryService.LoadResult(output)!;
    Check(report.Frames.Count == result.Frames.Count && report.Frames[0].Refused && report.Summary == result.Summary, "report.json includes new fields");
    var markdown = File.ReadAllText(Path.Combine(output, "summary.md"));
    Check(markdown.Contains("## 总结") && markdown.Contains("拒绝描述"), "summary.md shows summary and refused frames");
    Check(Directory.GetFiles(Path.Combine(output, "frames")).Length == result.Frames.Count, "frame images written for non-aborted job");
    var providerId = engine.Settings.OnlineAi.Providers[0].Id;
    Check(result.Frames.All(frame => frame.FramePath is { Length: > 0 } path && path.StartsWith("frames/") && File.Exists(Path.Combine(output, path))),
        "FramePath points at saved frame image");
    Check(result.Frames.All(frame => frame.ProviderId == providerId && frame.ModelId == "moondream" && frame.Model.Contains("moondream")) &&
        result.SummaryProviderId == providerId && result.SummaryModelId == "moondream" && result.SummaryModel!.Contains(" · "), "job records provider/model ids");
    Check(report.Frames.Select(frame => (frame.FramePath, frame.ProviderId, frame.ModelId)).SequenceEqual(result.Frames.Select(frame => (frame.FramePath, frame.ProviderId, frame.ModelId))) &&
        report.SummaryProviderId == result.SummaryProviderId && report.SummaryModelId == result.SummaryModelId, "report.json round-trips FramePath and ids");

    // 7. Old report.json without FramePath / ProviderId / ModelId / SummaryProviderId / SummaryModelId still loads.
    var legacy = Path.Combine(root, "legacy-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(legacy);
    File.WriteAllText(Path.Combine(legacy, "report.json"), """
        {"Source":"old.mp4","Duration":6,"TranscriptSource":"","Language":"zh","Models":["Ollama · moondream"],"SubtitleCount":0,
         "Frames":[],"Sections":[],"SegmentNotes":[],"Limitations":[],
         "Result":{"Frames":[{"Timestamp":"00:00:01","Description":"成年人。","Refused":false,"Model":"Ollama · moondream"},
                             {"Timestamp":"00:00:03","Description":null,"Refused":true,"Model":"Ollama · moondream"}],
                   "Summary":"旧总结。","SummaryModel":"Ollama · moondream","Aborted":false,"AbortReason":null,"SummaryRefused":false}}
        """);
    var old = VideoSummaryService.LoadResult(legacy);
    Check(old is { Summary: "旧总结。", SummaryModel: "Ollama · moondream", SummaryProviderId: null, SummaryModelId: null, Frames.Count: 2 } &&
        old.Frames[1].Refused && old.Frames.All(frame => frame is { FramePath: null, ProviderId: null, ModelId: null, Model: "Ollama · moondream" }),
        "old report.json without new fields loads with nulls");
    Console.WriteLine("ALL MOCK TESTS PASSED");
    return 0;
}

if (mode == "live")
{
    var store = new ModelStore();
    if (!await store.IsInstalledAsync(ModelCatalog.JoyTagId) && Environment.GetEnvironmentVariable("VS_JOYTAG_DIR") is { Length: > 0 } local)
        await store.ImportAsync(ModelCatalog.JoyTagId, [Path.Combine(local, ModelCatalog.JoyTagFile), Path.Combine(local, ModelCatalog.JoyTagLabels)]);
    if (!await store.IsInstalledAsync(ModelCatalog.JoyTagId)) throw new Exception("JoyTag not installed (set VS_JOYTAG_DIR to import local files)");
    var visionModel = Environment.GetEnvironmentVariable("VS_VISION") ?? "moondream";
    var textModel = Environment.GetEnvironmentVariable("VS_TEXT") ?? visionModel;
    var engine = new MediaEngine(Settings(visionModel, textModel));
    var video = await SyntheticVideoAsync();
    var output = Path.Combine(root, "live-" + DateTime.Now.ToString("HHmmss"));
    var options = Options(3); options.VisionModel = visionModel; options.SummaryModel = textModel;
    var job = SummaryJob(video, output, options);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var result = await new VideoSummaryService(engine, store).ExecuteAsync(job, _ => { }, CancellationToken.None);
    Console.WriteLine($"SECONDS: {watch.Elapsed.TotalSeconds:0}");
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine("FILES: " + string.Join(",", Directory.GetFileSystemEntries(output).Select(Path.GetFileName)));
    Check(!result.Aborted && result.Frames.Count > 0, "live pipeline ran");
    return 0;
}
throw new ArgumentException("mode must be mock or live");

static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

sealed class MockModel(Func<int, string> reply) : ISummaryModel
{
    private int _calls;
    public List<string> Prompts { get; } = []; public List<string> Systems { get; } = [];
    public string Backend => "mock";
    public string ModelId => "mock";
    public Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null, int tokens = 1024,
        JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
    { Systems.Add(system); Prompts.Add(prompt); return Task.FromResult(reply(++_calls)); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class MockTagger(Func<int, float> childScore) : IVideoFrameTagger
{
    public int Seen { get; private set; }
    public Task<IReadOnlyList<IReadOnlyDictionary<string, float>>> TagAsync(IReadOnlyList<byte[]> frames, CancellationToken ct)
    {
        Seen += frames.Count;
        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, float>>>(frames.Select((_, index) =>
            (IReadOnlyDictionary<string, float>)new Dictionary<string, float> { ["child"] = childScore(index), ["loli"] = 0f }).ToList());
    }
}
