using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

internal static class QueueListChecks
{
    public static void Run(string output, Action<bool, string> check, Action<int> pump)
    {
        var root = Path.Combine(output, "queue-list"); Directory.CreateDirectory(root);
        var engine = new MediaEngine(new());
        var video = Path.Combine(root, "演示视频.mkv");
        var portrait = Path.Combine(root, "竖屏视频.mkv");
        Generate(video, "testsrc2=size=320x180:rate=25");
        Generate(portrait, "testsrc2=size=90x160:rate=25");
        var document = Path.Combine(root, "说明.txt"); File.WriteAllText(document, "queue fixture");
        var jobs = new Job[]
        {
            new() { Inputs = [video], Output = Path.Combine(root,"导出.mp4"), Options = new() { Start = .4, End = 1.4, Width = 640, Height = 360, VideoCodec = "libx264" } },
            new() { FeatureId = "rotate", Inputs = [portrait], Output = Path.Combine(root,"竖屏.mp4"), State = JobState.Running, Progress = 42, Options = new() { Rotation = 90 } },
            new() { Inputs = [video], Output = video, State = JobState.Completed },
            new() { Inputs = [Path.Combine(root,"不存在.mp4")], Output = Path.Combine(root,"失败.mp4"), State = JobState.Failed, Error = "无法读取源文件，请检查文件位置。" },
            new() { FeatureId = "text-pdf", Inputs = [document], Output = Path.Combine(root,"说明.pdf") }
        };
        var store = new Storage(Path.Combine(root,"state"));
        store.SaveSettings(new() { NotifyComplete = false, ReduceMotion = true }); store.SaveJobs(jobs);
        var main = new MainWindow(store, engine); main.Show(); pump(30);
        var list = main.FindControl<ListBox>("JobList")!;
        var actual = list.Items.Cast<Job>().ToArray();
        // Storage recovers interrupted jobs; exercise a live state after opening.
        actual[1].State = JobState.Running;
        WaitRows();
        var rows = main.GetVisualDescendants().OfType<JobRowView>().ToArray();
        check(rows.Length == 5, "The real queue materializes every fixture row");
        var first = rows.Single(r => ReferenceEquals(r.Details?.Job,actual[0]));
        var second = rows.Single(r => ReferenceEquals(r.Details?.Job,actual[1]));
        check(first.Details!.Cover is not null && second.Details!.Cover is not null, "Landscape and portrait videos load real FFmpeg covers");
        check(first.Details.MediaSummary.Contains("320 × 180") && first.Details.MediaSummary.Contains("25 fps") && first.Details.MediaSummary.Contains("00:02"), "Task rows display actual duration, dimensions and frame rate");
        check(first.Details.CodecSummary.Contains("48 kHz") && first.Details.FileSummary.Contains("KB"), "Codec, audio sample rate and file size are read from the source");
        check(first.Details.SettingsSummary.Contains("H.264") && first.Details.SettingsSummary.Contains("640 × 360") && first.Details.SettingsSummary.Contains("截取"), "Output summary includes encoding, resolution and clip range");
        check(first.Details.SourceTip.Contains(video) && first.Details.OutputName == "导出.mp4", "Full paths remain available in tooltips while rows show short filenames");
        check(second.GetVisualDescendants().OfType<ProgressBar>().Single().IsVisible && !first.GetVisualDescendants().OfType<ProgressBar>().Single().IsVisible, "Only running tasks show a progress bar");
        check(!second.Details!.StateDetail.Contains("42.0%") && second.Details.StateText.Contains("42.0%"), "The status percentage appears only once in each task row");
        var done = rows.Single(r => ReferenceEquals(r.Details?.Job,actual[2]));
        check(done.Details!.StateText.Contains("已完成") && done.Details.StateText.Contains("KB"), "Completed rows show the actual output size");
        var failed = rows.Single(r => ReferenceEquals(r.Details?.Job,actual[3]));
        check(failed.Details!.Cover is null && failed.Details.StateDetail.Contains("无法读取") && failed.Details.MediaSummary.Contains("缺失"), "Missing files use the fallback icon and display the actionable failure reason");
        check(rows.Single(r => ReferenceEquals(r.Details?.Job,actual[4])).Details!.MediaSummary == "文件任务", "Document tasks do not launch media probing");
        actual[0].Inputs = [video, portrait]; pump(20);
        check(first.Details.Name.Contains("+1") && first.Details.MediaSummary.StartsWith("首个：") && first.Details.FileSummary.Contains("2 个文件"), "Multi-input rows identify the first media and total file count");
        actual[0].Options = new() { Format = "mkv", CopyStreams = true }; list.SelectedItem = actual[0]; pump(30); WaitRows();
        check(first.Details.SettingsSummary.Contains("直接复制流") && !first.Details.SettingsSummary.Contains("640 × 360"), "Editing job options refreshes the existing row without stale settings");
        foreach (var skin in new[] { "Light", "Dark", "MacOS9" })
        {
            Skin.Apply(skin); pump(40); WaitRows();
            rows = main.GetVisualDescendants().OfType<JobRowView>().ToArray();
            first = rows.Single(r => ReferenceEquals(r.Details?.Job,actual[0]));
            check(first.Details!.HasCover, skin + ": switching skins retains a loaded cover");
            foreach (var width in new[] { 1440, 1050 })
            {
                main.Width = width; main.Height = 860; pump(40);
                var image = first.FindControl<Image>("CoverImage")!;
                var cover = image.TranslatePoint(new Point(image.Bounds.Width,image.Bounds.Height), main);
                var right = first.TranslatePoint(new Point(first.Bounds.Width,first.Bounds.Height), main);
                main.CaptureRenderedFrame()!.Save(Path.Combine(root,$"queue-{skin}-{width}.png"));
                check(cover is { } p && p.X < main.Bounds.Width && right is { } q && q.X <= main.Bounds.Width && first.Bounds.Width > 400, skin + ": queue content fits at width " + width);
            }
        }
        var serialized = JsonSerializer.Serialize(actual);
        check(!serialized.Contains("Cover") && !serialized.Contains("MediaSummary"), "Queue export never embeds thumbnail pixels or inspection data");
        main.Close(); pump(30);
        check(rows.All(r => r.Details is null), "Closing the queue releases row subscriptions and cover bitmaps");
        var fallbackStore = new Storage(Path.Combine(root,"fallback-state"));
        fallbackStore.SaveJobs([new() { Inputs = [video], Output = Path.Combine(root,"fallback.mp4") }]);
        main = new MainWindow(fallbackStore, new MediaEngine(new() { FFprobePath = Path.Combine(root,"missing-ffprobe.exe") }));
        main.Show(); pump(20); WaitRows();
        var fallback = main.GetVisualDescendants().OfType<JobRowView>().Single();
        check(fallback.Details!.NoCover && fallback.Details.MediaSummary.Contains("外部工具") && fallback.Details.Job.State == JobState.Waiting,
            "Unavailable probing tools fall back gracefully without failing the conversion job");
        main.Close(); pump(20);

        void Generate(string path, string source)
        {
            var result = Task.Run(() => ProcessRunner.Run(engine.FFmpeg,["-v","error","-y","-f","lavfi","-i",source,"-f","lavfi","-i","sine=sample_rate=48000","-t","2","-c:v","ffv1","-c:a","pcm_s16le",path])).GetAwaiter().GetResult();
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        }
        void WaitRows()
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                pump(10);
                var ready = main.GetVisualDescendants().OfType<JobRowView>().Select(r => r.Ready).ToArray();
                if (ready.Length > 0 && ready.All(t => t.IsCompleted)) { foreach (var task in ready) task.GetAwaiter().GetResult(); break; }
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Queue previews did not settle.");
            }
        }
    }
}
