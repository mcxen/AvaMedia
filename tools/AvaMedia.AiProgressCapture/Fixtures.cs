using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;
using SkiaSharp;

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

    // Recognisable people and garments in a furnished room, confined to screenshot fixtures.
    private static byte[] PersonImage(int outfit, int frame = 0)
    {
        using var bitmap = new SKBitmap(360, 540);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = true };
        void Rect(float x, float y, float w, float h, string color)
        { paint.Color = SKColor.Parse(color); canvas.DrawRect(x, y, w, h, paint); }
        void Oval(float x, float y, float w, float h, string color)
        { paint.Color = SKColor.Parse(color); canvas.DrawOval(new SKRect(x, y, x + w, y + h), paint); }
        void Shape(string color, params (float X, float Y)[] points)
        {
            using var path = new SKPath(); path.MoveTo(points[0].X, points[0].Y);
            foreach (var point in points.Skip(1)) path.LineTo(point.X, point.Y);
            path.Close(); paint.Color = SKColor.Parse(color); canvas.DrawPath(path, paint);
        }
        Rect(0, 0, 360, 540, "#EDE3D6"); Rect(0, 406, 360, 134, "#BBA082");
        Rect(22, 35, 85, 148, "#FFF9F1"); Rect(30, 43, 69, 132, "#BCD5DE");
        Rect(62, 43, 4, 132, "#FFF9F1"); Rect(30, 107, 69, 4, "#FFF9F1");
        Rect(277, 54, 56, 79, "#997B61"); Rect(282, 59, 46, 69, "#D2BE9A");
        Rect(12, 337, 112, 83, "#BAAC99"); Rect(6, 315, 124, 27, "#C8BAA5");
        Rect(274, 299, 68, 118, "#CDB89B"); Rect(283, 418, 5, 30, "#806B54");
        Oval(278, 193, 53, 92, "#65846A"); Rect(299, 271, 15, 28, "#9C7D64");
        Oval(109, 487, 153, 21, "#99826D");
        canvas.Save(); canvas.Translate((frame - 2) * 3, frame % 2 * 2);
        Shape("#45627F", (146, 300), (183, 300), (180, 478), (144, 478));
        Shape("#36516F", (185, 300), (218, 300), (232, 478), (198, 478));
        Oval(132, 472, 53, 22, "#EDE8E0"); Oval(194, 472, 52, 22, "#EDE8E0");
        string[] garments = ["#FAFAF5", "#303034", "#4A78C2", "#C2484A", "#7AA35A"];
        var garment = garments[outfit % garments.Length];
        Shape("#DDB699", (126, 183), (142, 198), (118, 304), (101, 297));
        Shape("#DDB699", (219, 187), (231, 180), (260, 295), (243, 305));
        if (outfit == 1)
            Shape(garment, (145, 158), (213, 158), (219, 233), (254, 389), (114, 389), (142, 233));
        else
            Shape(garment, (144, 151), (172, 140), (195, 140), (221, 153), (243, 194), (219, 209), (216, 307), (139, 307), (139, 204), (116, 195));
        if (outfit == 3) Shape(garment, (139, 278), (219, 278), (250, 403), (113, 403));
        Rect(172, 126, 22, 29, "#D6AA8C");
        Oval(153, 57, 59, 81, "#46382F"); Oval(160, 73, 45, 61, "#E4BC9C");
        Shape("#46382F", (154, 85), (164, 56), (195, 58), (212, 87), (185, 73), (165, 83));
        Oval(169, 94, 4, 3, "#44362E"); Oval(190, 94, 4, 3, "#44362E");
        Rect(178, 115, 11, 2, "#B8796A");
        if (outfit == 0)
        {
            Shape("#DDDCD6", (164, 145), (181, 166), (179, 146));
            Shape("#DDDCD6", (198, 145), (181, 166), (185, 145));
            Rect(180, 166, 2, 132, "#DDDCD6");
            for (var y = 183; y < 290; y += 23) Oval(183, y, 3, 3, "#ABA9A3");
        }
        canvas.Restore();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public async Task<Job> ClassificationJobAsync()
    {
        var now = DateTime.UtcNow;
        var folder = Directory.CreateDirectory(Path.Combine(_root, "2026春夏_外拍")).FullName;
        string[] names = ["01 白色 · 衬衫 · 牛仔裤", "02 黑色 · 连衣裙", "03 蓝色 · 外套", "04 红色 · 半身裙", "05 绿色 · 外套"];
        var covers = Enumerable.Range(0, names.Length).Select(i => PersonImage(i)).ToArray();
        var frameTimes = Enumerable.Range(0, 6).Select(i => (double)i).ToArray();
        for (var i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(folder, $"candidate-{i:00}.png"), PersonImage(0, i));
        var currentPath = Path.Combine(folder, "DSC_0518.mp4");
        var encoded = await ProcessRunner.Run(MediaEngine.Resolve("", "ffmpeg"),
            ["-v", "error", "-framerate", "1", "-i", Path.Combine(folder, "candidate-%02d.png"),
                "-c:v", "mpeg4", "-q:v", "2", "-threads", "1", "-y", currentPath]);
        if (encoded.ExitCode != 0) throw new IOException("Fixture video: " + encoded.Error);
        var rule = new FolderClassificationRule("outfit", "相似服装", []) { ByOutfit = true };
        MediaTagScore[] scores = [new("shirt", .94, .94), new("jeans", .88, .88), new("white_shirt", .77, .77)];
        var appearance = new OutfitAppearanceFrame(0, 194400, .97, [.48, .08, 0, .29, 0, 0, 0, 0, .15, 0, 0, 0])
        {
            Encoder = OutfitAppearanceService.Encoder, Embedding = new double[384], BackgroundEmbedding = new double[384],
            BackgroundColors = [.1, .1, 0, .1, .5, 0, 0, 0, .1, .1, 0, 0], BackgroundRegions = new double[1536], PersonPixels = 81648
        };
        var files = Enumerable.Range(0, 1284).Select(i =>
        {
            var group = i < 85 ? 0 : i < 148 ? 1 : i < 198 ? 2 : i < 235 ? 3 : 4;
            if (i is >= 407 and <= 411) group = i - 407;
            var path = i == 412 ? currentPath : Path.Combine(folder, $"IMG_{i + 1:0000}.png");
            if (i != 412 && i <= 414) File.WriteAllBytes(path, covers[group]);
            if (i > 412) return new FolderClassificationTaskFile(path);
            var times = i == 412 ? frameTimes : [0d];
            var category = "group-" + (i == 412 ? 0 : group);
            var media = new MediaTagResult(path, scores, times.Length, 1, "GPU · JoyTag", new FileInfo(path).Length, now)
            {
                Frames = times.Select(time => new MediaTagFrame(time, scores)).ToArray(),
                OutfitFrames = times.Take(i == 412 ? 3 : 1).Select(time => appearance with { Seconds = time }).ToArray(),
                Scenes = new("fixture-scene", "GPU", null, .5, .04, [new("室内", "indoor", .83, times.Length)], []),
                DurationSeconds = i == 412 ? 6 : 0
            };
            var decision = new FolderClassificationDecision(rule.Id, rule.Name, category, names[i == 412 ? 0 : group],
                [new(category, names[i == 412 ? 0 : group], .89, 1)], times.Select(time => new FolderFrameClassification(time, category, .89, .17)).ToArray(),
                null, 1, "同次拍摄内按服装标签、颜色和图像特征分组")
                { ByOutfit = true, OutfitShootName = "拍摄组01" };
            return new FolderClassificationTaskFile(path, new(media, [decision], ["人物", "室内", "上衣"]), Pending: i == 412);
        }).ToArray();
        var activity = new AiActivity("识别服装区域", "MediaPipe · DINOv2", now.AddSeconds(-252), now)
        {
            Detail = currentPath, Current = 4, Total = 6, Unit = "帧", CurrentNode = 1,
            Nodes = [new("准备模型", new("准备模型", "JoyTag · DINOv2", now.AddSeconds(-252), now.AddSeconds(-228))
                { State = AiActivityState.Completed }), new("逐个文件分析")]
        };
        return new Job
        {
            FeatureId = "folder-classification", DownloadTitle = "2026春夏_外拍", State = JobState.Running,
            Inputs = files.Select(file => file.Path).ToArray(), Progress = 33,
            ProgressDetail = "识别服装区域 · 4 / 6 · 412 / 1284", ClassificationStartedUtc = now.AddSeconds(-252),
            ClassificationSnapshot = new(files), Activity = activity,
            ClassificationRegroup = new(34, new Dictionary<string, int> { ["outfit/group-0"] = 3, ["outfit/group-1"] = 2, ["outfit/group-2"] = 1 }, now),
            Options = new() { FolderClassification = new() { Rules = [rule], Analysis = new() { RecognizeScenes = true },
                OutputFolder = Path.Combine(_root, "已整理"), SourceFolders = [folder] } }
        };
    }
}
