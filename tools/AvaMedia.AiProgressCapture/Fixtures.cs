using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

internal sealed class Fixtures
{
    private readonly string _root;
    public string[] SpeechFiles { get; }
    public byte[][] Covers { get; }
    public AudioEnvelope Envelope { get; } = new(1600, 2000, Enumerable.Range(0, 2000)
        .Select(i => (float)(i % 71 < 9 ? .025 : .12 + .68 * Math.Abs(Math.Sin(i * .73) * Math.Cos(i * .17)))).ToArray());

    public Fixtures(string root)
    {
        _root = root;
        Covers = new[] { "#7E9DC2", "#C4B7A5", "#699F94", "#BE8A91", "#9893BB" }.Select(SolidImage).ToArray();
        SpeechFiles = new[] { "视频访谈_0312.mp4", "播客_第12期.mp4", "发布会回放.mp4" }
            .Select(name => Path.Combine(root, name)).ToArray();
        // Selection requires existing paths. All probes and thumbnails use FixtureEngine.
        foreach (var file in SpeechFiles) File.WriteAllBytes(file, []);
    }

    private static byte[] SolidImage(string color)
    {
        var surface = new Border { Width = 320, Height = 180, Background = Brush.Parse(color) };
        surface.Measure(new Size(320, 180)); surface.Arrange(new Rect(0, 0, 320, 180));
        using var bitmap = new RenderTargetBitmap(new PixelSize(320, 180), new Vector(96, 96));
        bitmap.Render(surface);
        using var stream = new MemoryStream(); bitmap.Save(stream); return stream.ToArray();
    }

    public Job SpeechJob()
    {
        var now = DateTime.UtcNow;
        var started = now.AddSeconds(-146);
        var texts = new[] { "我们今天聊一聊这次旅行的经历。", "从成都出发以后，沿途的风景一直在变化。", "这也是我第一次用视频记录完整的旅程。",
            "我希望把这些细节分享给大家。", "接下来我们会介绍拍摄时使用的方法。", "光线和声音，都是记录现场的重要部分。" };
        var cues = Enumerable.Range(0, 186).Select(i =>
        {
            var seconds = i * 3.96;
            return new SubtitleCue(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds + 5.8), texts[i % texts.Length]);
        }).ToArray();
        cues[^5] = new(TimeSpan.FromSeconds(721), TimeSpan.FromSeconds(725), "我们这一季的主题其实是从城市通勤出发的。");
        cues[^4] = new(TimeSpan.FromSeconds(725), TimeSpan.FromSeconds(729), "面料选了更轻的棉麻混纺，透气，也更好打理。");
        cues[^3] = new(TimeSpan.FromSeconds(730), TimeSpan.FromSeconds(734), "颜色上保留了白色和浅蓝，作为整个系列的基调。");
        cues[^2] = new(TimeSpan.FromSeconds(735), TimeSpan.FromSeconds(738), "第二组会加入一些更跳的红色单品。");
        cues[^1] = new(TimeSpan.FromSeconds(739), TimeSpan.FromSeconds(742), "未检测到语音");
        var recognition = new AiActivity("识别语音", "Whisper · base", started.AddSeconds(22), now)
        {
            Backend = "GPU · Whisper", Current = 742, Total = 1600, Unit = "秒", ResultCount = cues.Length,
            RecentResults = cues.TakeLast(6).Select(c => c.Text).ToArray(),
            RecentStages = ["00:00:23 · 第 1 / 6 段", "00:00:58 · 第 2 / 6 段", "00:01:46 · 第 3 / 6 段", "00:01:47 · 检测语音", "00:01:49 · 识别语音"]
        };
        var job = new Job
        {
            FeatureId = "auto-subtitle", State = JobState.Running, Progress = 43,
            // A missing source keeps the real waveform loader idle; Envelope is injected by the harness.
            Inputs = [Path.Combine(_root, "progress", "视频访谈_0312.mp4")], Output = Path.Combine(_root, "视频访谈_0312.srt"),
            Options = new() { Format = "srt", Transcription = new() { Model = SpeechModel.Base, Language = "zh", RecognitionOnly = true } },
            SubtitleResult = new(cues, 0, now) { IsPartial = true },
            Activity = recognition with
            {
                StartedUtc = started, CurrentNode = 2,
                Nodes = [new("读取音轨", new("读取音轨", "FFmpeg", started, started.AddSeconds(2))
                    { State = AiActivityState.Completed, RecentStages = ["00:00:00 · 检查音频与时长", "00:00:02 · 读取音轨"] }),
                    new("语音模型", new("加载语音模型", "Whisper · base", started.AddSeconds(2), started.AddSeconds(22))
                    { State = AiActivityState.Completed, RecentStages = ["00:00:03 · 下载语音模型 · 60 MB", "00:00:19 · 加载语音模型 · Whisper", "00:00:21 · 加载语音模型 · Silero VAD"] }),
                    new("语音识别", recognition), new("保存结果")]
            }
        };
        var segments = Enumerable.Range(0, 6).Select(i => new SpeechSegmentProgress(i, i * 300, Math.Min(1600, (i + 1) * 300),
            i < 2 ? "done" : i == 2 ? "running" : "pending", i < 2 ? 300 : i == 2 ? 142 : 0,
            i < 3 ? 9 : 0, i < 2 ? 76 : i == 2 ? 34 : 0)).ToArray();
        AiProgressStore.SetSpeech(job.Id.ToString(), new(1600, 300, 2, segments,
            Enumerable.Range(0, 54).Select(i => new VoiceInterval(i / 18 * 300 + i % 18 * 16 + 1,
                i / 18 * 300 + i % 18 * 16 + 11)).ToArray(), cues.Length, "识别语音"));
        return job;
    }

    public Job ClassificationJob()
    {
        var now = DateTime.UtcNow;
        var folder = Directory.CreateDirectory(Path.Combine(_root, "相机导入_2026-10")).FullName;
        string[] names = ["蓝色上衣", "白色衬衫", "绿色外套", "粉色上衣", "浅紫套装"];
        var rule = new FolderClassificationRule("outfit", "相似服装", []) { ByOutfit = true };
        MediaTagScore[] scores = [new("person", .98, .99), new("blue_shirt", .92, .96), new("outdoors", .89, .94)];
        var appearance = new OutfitAppearanceFrame(0, 57600, .97, [.1, .04, 0, .74, .02, 0, 0, 0, .02, .08, 0, 0])
        {
            Encoder = OutfitAppearanceService.Encoder, Embedding = new double[384], BackgroundEmbedding = new double[384],
            BackgroundColors = [.1, .1, 0, .1, .5, 0, 0, 0, .1, .1, 0, 0], BackgroundRegions = new double[1536], PersonPixels = 34000
        };
        var files = Enumerable.Range(0, 1284).Select(i =>
        {
            var path = Path.Combine(folder, $"IMG_{i + 1:0000}.png");
            if (i <= 412) File.WriteAllBytes(path, Covers[i % Covers.Length]);
            if (i > 412) return new FolderClassificationTaskFile(path);
            var category = "group-" + i % names.Length;
            var media = new MediaTagResult(path, scores, 1, 1, "CPU · JoyTag", Covers[i % Covers.Length].Length, now)
            { Frames = [new(0, scores)], OutfitFrames = i < 412 ? [appearance] : [] };
            var decision = new FolderClassificationDecision(rule.Id, rule.Name, category, names[i % names.Length],
                [new(category, names[i % names.Length], .89, 1)], [new(0, category, .89, .17)], null, 1, "服装颜色与纹理相似")
                { ByOutfit = true, OutfitShootName = "城市街拍" };
            return new FolderClassificationTaskFile(path, new(media, [decision], ["人物", "户外", "上衣"]), Pending: i == 412);
        }).ToArray();
        var activity = new AiActivity("识别服装区域", "MediaPipe · DINOv2", now.AddSeconds(-252), now)
        {
            Detail = files[412].Path, Current = 1, Total = 1, Unit = "帧", CurrentNode = 1, Preview = Covers[2],
            RecentStages = ["00:00:00 · 准备模型", "00:00:12 · 识别图片标签", "00:04:10 · 识别服装区域"],
            RecentResults = ["IMG_0411.png · 蓝色上衣", "IMG_0412.png · 白色衬衫"],
            Nodes = [new("准备模型", new("准备模型", "JoyTag · DINOv2", now.AddSeconds(-252), now.AddSeconds(-240))
                { State = AiActivityState.Completed }), new("逐个文件分析")]
        };
        return new Job
        {
            FeatureId = "folder-classification", DownloadTitle = "相机导入_2026-10", State = JobState.Running,
            Inputs = files.Select(file => file.Path).ToArray(), Progress = 412d / 1284 * 100,
            ProgressDetail = "识别服装区域 · 1 / 1 · 412 / 1284", ClassificationStartedUtc = now.AddSeconds(-252),
            ClassificationSnapshot = new(files), Activity = activity,
            ClassificationRegroup = new(51, new Dictionary<string, int> { ["outfit/group-0"] = 3, ["outfit/group-1"] = 2 }, now),
            Options = new() { FolderClassification = new() { Rules = [rule], OutputFolder = Path.Combine(_root, "已整理"), SourceFolders = [folder] } }
        };
    }
}
