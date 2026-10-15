using System.Text.Json;
using AvaMedia.Core;

/// <summary>View projection units: synthetic probe/cue records and ordinary files, no media execution.</summary>
internal static class SubtitleTaskViewChecks
{
    private static int _checks;

    public static async Task RunAsync()
    {
        var previous = Environment.GetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY");
        var root = Path.Combine(Path.GetTempPath(), "AvaMedia-subtitle-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", root);
        try
        {
            await Plans(root);
            await PartialAndRestore(root);
            await CompleteAndReview(root);
            await SavedResultWithoutStages(root);
            Console.WriteLine($"Passed {_checks} subtitle view assertions.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", previous);
            Directory.Delete(root, true);
        }
    }

    private static async Task Plans(string root)
    {
        var job = NewJob(root);
        job.Options.Start = 15;
        job.Options.Transcription!.Language = "auto";
        using ISubtitleTask session = new SubtitleTaskSession(job, new Host());
        var view = session.View;
        Check(view.SourcePath == job.Inputs[0] && view.Info is null, "source is configured; unprobed media info stays null");
        Check(view.RangeStart == 15 && view.RangeEnd is null && view.RecognizedSeconds == 0, "whole-file end and unobserved progress are not estimated");
        Check(view.Lines.Count == 0 && !view.HasResult && view.IsPartial && !view.CanReview, "queued task has no result or review command");
        Check(view.Language == "" && view.ModelName == "Whisper Tiny", "automatic language stays unknown; model comes from configuration");
        foreach (var (format, label) in new[] { ("srt", "SRT"), ("ass", "ASS"), ("vtt", "VTT"), ("mp4", "烧录到视频"), ("invalid", "") })
        {
            job.Options.Format = format;
            await Until(session, () => session.View.OutputFormat == label);
            Check(session.View.OutputFormat == label, "format label " + format);
        }
        job.Options.Transcription.RecognitionOnly = true;
        await Until(session, () => session.View.Output.IsFixedLocation);
        Check(session.View.Output.Path == job.Output && session.View.Output.Folder == root
            && !session.View.Output.Exists && session.View.Output.Bytes is null, "internal output uses the actual path without invented bytes");
    }

    private static async Task PartialAndRestore(string root)
    {
        var job = NewJob(root); job.State = JobState.Running;
        job.Options.Start = 30; job.Options.End = 80; job.Options.Speed = 2;
        job.Options.Transcription!.Language = "auto";
        var info = new MediaInfo(65, 1920, 1080, true, true, "{\"probe\":true}", AudioStreamIndex: 1);
        using var state = new SubtitleTaskState(job);
        state.Start(SubtitleStageIds.ReadAudio);
        state.Source(info, 30, 65);
        state.Complete(SubtitleStageIds.ReadAudio, "已探测");
        state.Start(SubtitleStageIds.Recognize);
        state.Recognized(46);
        var first = Result(job, [Cue(1, 3, "第一行", "zh")], partial: true);
        state.Transcript(first); job.SubtitleResult = first;
        using (var session = new SubtitleTaskSession(job, new Host()))
        {
            var before = session.View;
            Check(before.Info == info && before.RangeStart == 30 && before.RangeEnd == 65, "probe info and effective source range project exactly");
            Check(before.RecognizedSeconds == 46 && before.Lines[0].Start == 1 && before.Lines[0].End == 3, "source progress and output cue clocks stay separate under seek/speed");
            Check(before.Lines[0].Number == 1 && before.Lines[0].Text == "第一行" && before.Lines[0].Language == "zh"
                && before.Language == "zh" && before.HasResult && before.IsPartial && !before.CanReview, "partial cue facts project without enabling review");
            var second = Result(job, [.. first.Cues, Cue(4, 5, "Second", "en"), Cue(6, 7, "未知语言", "auto")], partial: true);
            state.Recognized(58);
            state.Transcript(second); job.SubtitleResult = second;
            await Until(session, () => session.View.Lines.Count == 3);
            Check(session.Snapshot.State == MultiStageTaskState.Running && session.View.RecognizedSeconds == 58,
                "Changed observes the updated view and snapshot");
            Check(before.Lines.Count == 1 && session.View.Lines.Select(line => line.Number).SequenceEqual(new[] { 1, 2, 3 }), "partial appends preserve earlier immutable views");
            Check(session.View.Language == "zh、en" && session.View.Lines[2].Language == "", "only detected languages are reported, including multilingual results");
            Check(session.View.Lines is not SubtitleLineView[], "line collection cannot be cast to a mutable array");
            state.Finish(JobState.Cancelled); job.State = JobState.Cancelled;
            job.SubtitleStages = null; job.SubtitleResult = null;
            await Until(session, () => session.Snapshot.State == MultiStageTaskState.Cancelled && session.View.Lines.Count == 3);
        }
        // Simulate queue persistence across an app restart (execution-only fields are JsonIgnore).
        var restarted = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        using var restored = new SubtitleTaskSession(restarted, new Host());
        await Until(restored, () => restored.View.Lines.Count == 3);
        Check(restored.View.Info == info && restored.View.RangeEnd == 65 && restored.View.RecognizedSeconds == 58,
            "saved partial observation restores probe facts, clipped range and source progress after restart");
        Check(restored.View.IsPartial && !restored.View.CanReview && restored.View.Lines[1].Language == "en", "saved partial lines retain languages and review restrictions");
        restarted.State = JobState.Waiting;
        await Until(restored, () => restored.View.Lines.Count == 0);
        Check(restored.View.Info is null && restored.View.RecognizedSeconds == 0 && !restored.View.HasResult, "rerun hides previous execution observations");
    }

    private static async Task CompleteAndReview(string root)
    {
        var job = NewJob(root); job.State = JobState.Running;
        var file = new FileInfo(job.Inputs[0]);
        job.Options.Transcription!.ReviewedCues = [Cue(1, 2, "校对结果", "zh")];
        job.Options.Transcription.ReviewedSourceLength = file.Length;
        job.Options.Transcription.ReviewedSourceWriteUtc = file.LastWriteTimeUtc;
        var info = new MediaInfo(10, 0, 0, true, false, "{}");
        await new SpeechSubtitleService(new ProbeEngine(info)).ExecuteAsync(job, _ => { }, default);
        using (var session = new SubtitleTaskSession(job, new Host()))
        {
            await Until(session, () => session.View.Output.Exists);
            Check(session.View.Info == info && session.View.RangeEnd == 10 && session.View.RecognizedSeconds == 0,
                "reviewed execution records actual probe info without claiming recognition ran");
            Check(session.View.HasResult && !session.View.IsPartial && !session.View.CanReview, "complete transcript cannot be reviewed while queue execution remains active");
            job.State = JobState.Completed;
            await Until(session, () => session.View.CanReview);
            Check(session.View.Output.Bytes == new FileInfo(job.Output).Length && session.View.Output.Bytes > 0,
                "output existence and bytes come from the written file");
        }
        var restarted = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        using var restored = new SubtitleTaskSession(restarted, new Host());
        await Until(restored, () => restored.View.CanReview);
        Check(restored.View.Lines.Single().Text == "校对结果" && restored.View.Info == info && !restored.View.IsPartial,
            "complete saved result and execution metadata restore after restart");
        Check(restarted.SubtitleResult?.Cues.Single().Text == "校对结果", "restoration supplies the existing review entry point with the complete result");
        File.Delete(AiTaskResults.PathFor(job, "subtitles"));
        var checkpointJob = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        using (var checkpoint = new SubtitleTaskSession(checkpointJob, new Host()))
        {
            await Until(checkpoint, () => checkpoint.View.CanReview);
            Check(checkpointJob.SubtitleResult?.Cues.Single().Text == "校对结果", "complete observation checkpoint alone remains openable by the review window");
        }
        File.Delete(job.Output);
        await Until(restored, () => !restored.View.Output.Exists);
        Check(restored.View.Output.Bytes is null && restored.View.CanReview, "missing exported file clears bytes but saved transcript remains reviewable");
        File.AppendAllText(job.Inputs[0], "changed");
        await Until(restored, () => !restored.View.CanReview);
        Check(restored.View.HasResult && !restored.View.IsPartial, "changed source disables review without deleting the complete result");
        File.Delete(job.Inputs[0]);
        await Until(restored, () => !restored.View.CanReview);
        Check(!restored.View.CanReview, "missing source is not reviewable");
    }

    private static async Task SavedResultWithoutStages(string root)
    {
        var job = NewJob(root); job.State = JobState.Failed;
        var result = Result(job, [Cue(2, 4, "保存结果", "en")]);
        await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "subtitles"), result, default);
        using (var session = new SubtitleTaskSession(job, new Host()))
        {
            await Until(session, () => session.View.CanReview);
            Check(session.View.Info is null && session.View.RangeEnd is null && session.View.RecognizedSeconds == 0,
                "standalone saved transcript does not invent missing probe or recognition facts");
            Check(session.View.Language == "en" && session.View.HasResult && !session.View.IsPartial,
                "saved cue languages take precedence over configured language");
        }
        await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "subtitles"), result with { Cues = [] }, default);
        job.SubtitleResult = null;
        using (var empty = new SubtitleTaskSession(job, new Host()))
        {
            await Until(empty, () => !empty.View.IsPartial);
            Check(!empty.View.HasResult && !empty.View.CanReview, "an empty complete transcript has no reviewable lines");
        }
        File.WriteAllText(AiTaskResults.PathFor(job, "subtitles"), "{interrupted");
        job.SubtitleResult = null;
        using var malformed = new SubtitleTaskSession(job, new Host());
        await Until(malformed, () => malformed.Snapshot.Revision > 1);
        Check(!malformed.View.HasResult && malformed.View.IsPartial && !malformed.View.CanReview, "malformed saved results leave unknown facts empty");
    }

    private static SubtitleCue Cue(double start, double end, string text, string language) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text) { Language = language };
    private static SubtitleTaskResult Result(Job job, SubtitleCue[] cues, bool partial = false)
    {
        var file = new FileInfo(job.Inputs[0]);
        return new(cues, file.Length, file.LastWriteTimeUtc) { IsPartial = partial };
    }
    private static Job NewJob(string root)
    {
        var source = Path.Combine(root, Guid.NewGuid() + ".txt"); File.WriteAllText(source, "fingerprint fixture");
        return new() { FeatureId = "auto-subtitle", Inputs = [source], Output = Path.Combine(root, Guid.NewGuid() + ".srt"),
            Options = new() { Format = "srt", Transcription = new() { Model = SpeechModel.Tiny, Language = "zh" } } };
    }
    internal static async Task Until(ISubtitleTask session, Func<bool> condition)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            try { if (condition()) changed.TrySetResult(); }
            catch (Exception error) { changed.TrySetException(error); }
        }
        session.Changed += OnChanged;
        try
        {
            OnChanged(null, EventArgs.Empty);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { session.Changed -= OnChanged; }
    }
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        _checks++; Console.WriteLine("PASS " + label);
    }
    private sealed class Host : IMultiStageQueueHost
    {
        public bool CanStart(Job job) => false;
        public bool CanPause(Job job) => false;
        public bool CanResume(Job job) => false;
        public bool CanStop(Job job) => false;
        public bool CanRerun(Job job) => false;
        public bool CanChangeOutput(Job job) => false;
        public Task StartAsync(Job job) => Task.CompletedTask;
        public void Pause(Job job) { }
        public Task ResumeAsync(Job job) => Task.CompletedTask;
        public void Stop(Job job) { }
        public Task RerunAsync(Job job, string? fromStageId) => Task.CompletedTask;
        public Task ChangeOutputFolderAsync(Job job, string folder) => Task.CompletedTask;
    }
    private sealed class ProbeEngine(MediaInfo info) : IMediaEngine
    {
        public AppSettings Settings { get; } = new();
        public string FFmpeg => throw new Exception("View unit must not invoke FFmpeg.");
        public string FFprobe => throw new Exception("View unit must not invoke FFprobe.");
        public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0) => Task.FromResult(info);
        public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => throw new NotSupportedException();
        public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => throw new NotSupportedException();
    }
}
