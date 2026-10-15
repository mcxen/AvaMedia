using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaMedia.Core;
using Xunit;

public sealed class PersonClipEditTests
{
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AvaMedia-person-edits-" + Guid.NewGuid().ToString("N"));
        public Job Job { get; }
        public PersonDetectionTaskResult Detection { get; }
        public Files(IReadOnlyList<PersonClipSample>? samples = null, double analysed = 20, PersonClipOptions? options = null)
        {
            Directory.CreateDirectory(Root);
            var source = Path.Combine(Root, "source.mp4"); File.WriteAllText(source, "person editing source fingerprint");
            Job = new() { FeatureId = "person-clip", Inputs = [source], Output = Path.Combine(Root, "people.json"),
                Options = new() { Format = "json", PersonClip = new() { AnalysisOnly = true, Detection = options ?? new(MinimumSeconds: .5) } } };
            var file = new FileInfo(source);
            Detection = new(new(source, new(20, 640, 360, true, true, "{}", "h264", "aac", 48000, 2),
                [new() { Start = 1, End = 4 }, new() { Start = 6, End = 9 }, new() { Start = 12, End = 14 }],
                samples?.Count ?? 0, 0, 0, 0, "CPU") { Samples = samples, AnalyzedSeconds = analysed }, file.Length, file.LastWriteTimeUtc);
            Job.PersonDetectionResult = Detection;
        }
        public Task<IPersonClipEditSession> Open() => PersonClipEditing.OpenAsync(Job);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static IReadOnlyList<PersonClipSample> Scores() => Enumerable.Range(0, 40).Select(i =>
        new PersonClipSample(i / 2d, i is >= 2 and <= 7 ? .8f : .2f, i is >= 2 and <= 7 ? PersonClipSampleFlags.Person : PersonClipSampleFlags.None)).ToArray();
    private static string State(IPersonClipEditSession session) => JsonSerializer.Serialize(new
    { session.Segments, session.Exclusions, session.Spans, session.ModifiedCount, session.KeptCount, session.KeptSeconds });
    private static void RoundTripCommand(IPersonClipEditSession session, Action command)
    {
        var before = State(session); var events = 0;
        EventHandler handler = (_, _) => events++;
        session.Changed += handler;
        command(); var after = State(session);
        Assert.True(session.CanUndo); Assert.Equal(1, events);
        session.Undo(); Assert.Equal(before, State(session)); Assert.True(session.CanRedo); Assert.Equal(2, events);
        session.Redo(); Assert.Equal(after, State(session)); Assert.Equal(3, events);
        session.Changed -= handler;
    }

    [Fact]
    public async Task Samples_round_trip_compactly_in_results_cache_and_task_files()
    {
        var samples = Enumerable.Range(0, 378).Select(i => new PersonClipSample(i / 2d, (i % 1000) / 1000f, (PersonClipSampleFlags)(i % 128))).ToArray();
        using var files = new Files(samples);
        var json = JsonSerializer.Serialize(files.Detection);
        var payload = JsonDocument.Parse(json).RootElement.GetProperty("Result").GetProperty("Samples").GetRawText();
        Assert.InRange(Encoding.UTF8.GetByteCount(payload), 3500, 6500);
        var restored = JsonSerializer.Deserialize<PersonDetectionTaskResult>(json)!;
        Assert.Equal(samples, restored.Result.Samples);
        await AiTaskResults.SaveAsync(files.Job.Output, files.Detection, default);
        Assert.Equal(samples, (await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(files.Job.Output))!.Result.Samples);
        var options = files.Job.Options.PersonClip!.Detection;
        var cache = new PersonClipCache(files.Job.Inputs[0], options);
        await cache.WriteAsync(files.Detection.Result, default);
        var cached = await cache.ReadAsync(files.Detection.Result.Info, options, default);
        Assert.NotNull(cached); Assert.True(cached.FromCache); Assert.Equal(samples, cached.Samples);
        var partial = files.Detection.Result with { IsPartial = true, Samples = samples.Take(7).ToArray() };
        Assert.Equal(partial.Samples, JsonSerializer.Deserialize<PersonClipResult>(JsonSerializer.Serialize(partial))!.Samples);
    }

    [Fact]
    public async Task Old_json_without_samples_or_edits_still_opens_and_exports()
    {
        using var files = new Files();
        var node = JsonNode.Parse(JsonSerializer.Serialize(files.Detection))!;
        node["Result"]!.AsObject().Remove("Samples"); node.AsObject().Remove("Edits");
        await File.WriteAllTextAsync(files.Job.Output, node.ToJsonString()); files.Job.PersonDetectionResult = null;
        var session = await files.Open();
        Assert.False(session.HasScores); Assert.Empty(session.Samples); Assert.Null(session.Evidence(1));
        Assert.Equal(0, session.ModifiedCount); Assert.Equal(3, session.ToClipEditResult().Segments.Count);
        Assert.Null(session.Segments[0].Confidence); Assert.False(session.Segments[0].LowConfidence);
        files.Job.PersonDetectionResult = files.Detection with { Result = files.Detection.Result with { Samples = [] } };
        var empty = await files.Open(); Assert.False(empty.HasScores); Assert.Null(empty.Evidence(1));
        Assert.Equal(PersonClipSnapKind.DetectionSegment, empty.Snap(1, 0, 1)!.Kind);
    }

    [Fact]
    public void Frame_flags_keep_scores_in_checkpoint_and_mark_every_skip_and_boundary()
    {
        var frame = new PersonFrame(1.25, true, true, []) { Score = .4567f, Reused = true };
        var sample = frame.ToSample(true);
        Assert.Equal(.4567f, sample.Score);
        Assert.Equal(PersonClipSampleFlags.Person | PersonClipSampleFlags.Uncertain | PersonClipSampleFlags.Reused | PersonClipSampleFlags.Boundary, sample.Flags);
        var checkpointFrame = JsonSerializer.Deserialize<PersonFrame>(JsonSerializer.Serialize(frame with { Evidence = [] }))!;
        Assert.Equal(sample, checkpointFrame.ToSample(true));
        foreach (var (skip, flag) in new[] { (PersonFrameSkip.Dark, PersonClipSampleFlags.Dark), (PersonFrameSkip.Blank, PersonClipSampleFlags.Blank), (PersonFrameSkip.Excluded, PersonClipSampleFlags.Excluded) })
        {
            var skipped = new PersonFrame(2, false, false, []) { Skip = skip, Score = .9f }.ToSample(true);
            Assert.Equal(0, skipped.Score); Assert.False(skipped.Analysed); Assert.Equal(flag | PersonClipSampleFlags.Boundary, skipped.Flags);
        }
    }

    [Fact]
    public void Compact_converter_rounds_to_milliseconds_and_rejects_incomplete_or_invalid_values()
    {
        using var files = new Files([new(1.23456, .45678f, PersonClipSampleFlags.Person)]);
        var result = JsonSerializer.Deserialize<PersonClipResult>(JsonSerializer.Serialize(files.Detection.Result))!;
        Assert.Equal(1.235, result.Samples![0].Seconds); Assert.Equal(.457f, result.Samples[0].Score);
        foreach (var payload in new[] { "[1000,500]", "[-1,500,1]", "[0,1001,1]", "[0,500,128]", "[0,500,1,2]", "[0,\"500\",1]", "[0,500,null]" })
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(files.Detection.Result))!; node["Samples"] = JsonNode.Parse(payload);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PersonClipResult>(node.ToJsonString()));
        }
    }

    [Fact]
    public async Task Every_edit_command_is_one_undo_redo_step()
    {
        var commands = new Action<IPersonClipEditSession>[]
        {
            s => s.SetKeep(1, false), s => s.SetBounds(1, 1.5, 4), s => s.Split(1, 2.5),
            s => s.SplitMany(1, [new() { Start = 1, End = 2 }, new() { Start = 2, End = 3 }, new() { Start = 3, End = 4 }]),
            s => s.MergeWithNext(1), s => s.MergeWithPrevious(2), s => s.Add(4.5, 5.5), s => s.Duplicate(1),
            s => s.Move(2, -1), s => s.Move(2, 1),
            s => s.SetEffects(1, new() { Start = -10, End = 100, Speed = 1.5, Rotation = 90, FadeIn = .2 }),
            s => s.AddExclusion(2, 3), s => s.AddExclusion(1, 4), s => s.AddExclusion(0, 2), s => s.AddExclusion(3, 5)
        };
        foreach (var command in commands)
        {
            using var files = new Files(Scores()); var session = await files.Open();
            RoundTripCommand(session, () => command(session));
        }
        using var restoredFiles = new Files(); var restored = await restoredFiles.Open();
        restored.SetBounds(1, 1.5, 4); RoundTripCommand(restored, () => restored.RestoreSegment(1));
        var manual = restored.Add(4.5, 5.5); RoundTripCommand(restored, () => restored.RestoreSegment(manual));
        restored.SetKeep(1, false); RoundTripCommand(restored, restored.InvertKeep);
        restored.AddExclusion(2, 3); RoundTripCommand(restored, () => restored.RemoveExclusion(0));
        restored.Add(15, 17); RoundTripCommand(restored, restored.RestoreDetection);
    }

    [Fact]
    public async Task Modified_count_and_adjusted_confidence_are_relative_to_original_detection()
    {
        using var files = new Files(Scores()); var s = await files.Open();
        Assert.Equal(8, s.DetectedSeconds); Assert.Equal(0, s.ModifiedCount);
        Assert.InRange(s.Segments[0].Confidence!.Value, .799, .801); Assert.False(s.Segments[0].LowConfidence);
        Assert.True(s.Segments[1].LowConfidence);
        s.SetBounds(1, 1.5, 4); Assert.Equal(1, s.ModifiedCount); Assert.True(s.Segments[0].Adjusted);
        s.SetKeep(1, false); s.SetEffects(1, new() { Speed = 2 }); Assert.Equal(1, s.ModifiedCount);
        s.RestoreSegment(1); Assert.Equal(0, s.ModifiedCount); Assert.False(s.Segments[0].Adjusted);
        var right = s.Split(1, 2.5); Assert.Equal(2, s.ModifiedCount);
        s.RestoreSegment(right); Assert.Equal(3, s.Segments.Count); Assert.Equal(0, s.ModifiedCount);
        s.MergeWithNext(1); Assert.Equal(1, s.ModifiedCount);
        s.RestoreSegment(1); Assert.Equal(0, s.ModifiedCount); Assert.Equal(3, s.Segments.Count);
        var added = s.Add(15, 17); Assert.Equal(1, s.ModifiedCount); Assert.Equal(PersonClipSegmentOrigin.Manual, s.Segments.Last().Origin);
        s.RestoreSegment(added); Assert.Equal(0, s.ModifiedCount);
        s.Move(1, 1); Assert.Equal(2, s.ModifiedCount); s.RestoreDetection();
        s.AddExclusion(15, 17); Assert.Equal(1, s.ModifiedCount);
        s.AddExclusion(18, 19); Assert.Equal(2, s.ModifiedCount);
        s.RemoveExclusion(0); Assert.Equal(1, s.ModifiedCount); s.RestoreDetection(); Assert.Equal(0, s.ModifiedCount);
    }

    [Fact]
    public async Task Invalid_commands_are_atomic_and_report_Chinese_validation_messages()
    {
        using var files = new Files(); var s = await files.Open();
        var before = State(s);
        var invalid = new Action[] { () => s.SetBounds(1, -1, 4), () => s.SetBounds(1, 1, 21),
            () => s.SetBounds(1, double.NaN, 4), () => s.SetBounds(1, 1, 1.1), () => s.SetBounds(1, 2, 7),
            () => s.Add(3, 5), () => s.Add(20, 21), () => s.Split(1, 1), () => s.Split(1, 1.2),
            () => s.SplitMany(1, [new() { Start = 1, End = 2 }, new() { Start = 3, End = 4 }]),
            () => s.SetKeep(999, false), () => s.Move(1, -1), () => s.Move(1, 2), () => s.MergeWithPrevious(1),
            () => s.AddExclusion(0, 21), () => s.RemoveExclusion(0), () => s.InvertKeep() };
        foreach (var command in invalid)
        {
            var error = Assert.Throws<ArgumentException>(command); Assert.Matches("[\\u4e00-\\u9fff]", error.Message);
            Assert.Equal(before, State(s)); Assert.False(s.CanUndo); Assert.False(s.CanRedo);
        }
        s.SetKeep(1, false); Assert.Throws<ArgumentException>(() => s.Add(2, 3)); // Removed segments still occupy time.
        s.SetKeep(2, false); Assert.Throws<ArgumentException>(() => s.SetKeep(3, false));
        s.Undo(); Assert.True(s.CanRedo); before = State(s);
        Assert.Throws<ArgumentException>(() => s.SetBounds(3, 13, 21)); Assert.True(s.CanRedo); Assert.Equal(before, State(s));
    }

    [Fact]
    public async Task Undo_history_is_limited_to_100_and_ids_do_not_recycle_after_undo()
    {
        using var files = new Files(); var s = await files.Open(); var events = 0;
        s.Changed += (_, _) => events++;
        for (var i = 0; i < 105; i++) s.SetEffects(1, new() { Speed = 1 + i / 100d });
        for (var i = 0; i < 100; i++) s.Undo();
        Assert.False(s.CanUndo); Assert.True(s.CanRedo); Assert.Equal(205, events);
        s.Undo(); Assert.Equal(205, events);
        var id = s.Add(15, 16); Assert.False(s.CanRedo); s.Undo();
        var next = s.Add(17, 18); Assert.True(next > id);
    }

    [Fact]
    public async Task Minimum_length_uses_detection_setting_with_point_one_floor()
    {
        using var files = new Files(options: new(MinimumSeconds: 0)); var s = await files.Open();
        Assert.Equal(.1, s.MinimumSeconds); s.Add(15, 15.1);
        Assert.Throws<ArgumentException>(() => s.Add(16, 16.09));
        using var longer = new Files(options: new(MinimumSeconds: 1)); var s2 = await longer.Open();
        Assert.Equal(1, s2.MinimumSeconds); Assert.Throws<ArgumentException>(() => s2.Split(1, 1.5));
    }

    [Fact]
    public async Task Spans_cover_duration_and_classify_dark_blank_excluded_missing_and_removed()
    {
        var samples = Scores().Where(sample => sample.Seconds < 16 && sample.Seconds != 10).Select(sample => sample.Seconds switch
        {
            0 => sample with { Score = 0, Flags = PersonClipSampleFlags.Dark },
            .5 => sample with { Score = 0, Flags = PersonClipSampleFlags.Blank },
            _ => sample
        }).ToArray();
        using var files = new Files(samples, analysed: 16); var s = await files.Open();
        s.SetKeep(2, false); s.AddExclusion(14, 15);
        PersonClipSpan At(double time) => Assert.Single(s.Spans, span => span.Start <= time && span.End > time);
        Assert.Equal(PersonClipUnscannedReason.Dark, At(.25).Reason); Assert.Equal(PersonClipUnscannedReason.Blank, At(.75).Reason);
        Assert.Equal(PersonClipSpanKind.Kept, At(2).Kind); Assert.Equal(PersonClipSpanKind.NoPerson, At(5).Kind);
        Assert.Equal(PersonClipSpanKind.RemovedByUser, At(7).Kind); Assert.Equal(PersonClipUnscannedReason.Excluded, At(14.5).Reason);
        Assert.Equal(PersonClipUnscannedReason.NotAnalysed, At(10.2).Reason); Assert.Equal(PersonClipUnscannedReason.NotAnalysed, At(18).Reason);
        Assert.Equal(0, s.Spans[0].Start); Assert.Equal(20, s.Spans[^1].End);
        s.Add(17, 18); Assert.Equal(PersonClipSpanKind.Kept, At(17.5).Kind);
        s.SetKeep(1, false); Assert.Equal(PersonClipSpanKind.RemovedByUser, At(2).Kind);
        for (var i = 1; i < s.Spans.Count; i++) Assert.Equal(s.Spans[i - 1].End, s.Spans[i].Start);
        using var old = new Files(analysed: 16); var noScores = await old.Open(); noScores.AddExclusion(14, 15);
        Assert.All(noScores.Spans.Where(span => span.Kind == PersonClipSpanKind.Unscanned), span =>
            Assert.True(span.Reason is PersonClipUnscannedReason.Excluded or PersonClipUnscannedReason.NotAnalysed));
        Assert.Contains(noScores.Spans, span => span.Start == 16 && span.End == 20 && span.Reason == PersonClipUnscannedReason.NotAnalysed);
    }

    [Fact]
    public async Task Snap_uses_person_runs_and_nearest_edges_with_old_result_fallback()
    {
        using var files = new Files(Scores()); var s = await files.Open();
        var first = s.Snap(1.04, .1, 1); Assert.Equal(PersonClipSnapKind.FirstPersonFrame, first!.Kind); Assert.Equal("首个有人帧", first.Label); Assert.Equal(1, first.Seconds);
        var last = s.Snap(3.46, .1, 1); Assert.Equal(PersonClipSnapKind.LastPersonFrame, last!.Kind); Assert.Equal("末个有人帧", last.Label); Assert.Equal(3.5, last.Seconds);
        Assert.Null(s.Snap(4.01, .1, 1));
        Assert.Equal(PersonClipSnapKind.SegmentEdge, s.Snap(6.03, .1, 1)!.Kind);
        s.AddExclusion(15, 16); Assert.Equal(PersonClipSnapKind.Exclusion, s.Snap(15.03, .1)!.Kind);
        Assert.Null(s.Snap(10.5, .1)); Assert.Throws<ArgumentException>(() => s.Snap(1, -1));
        s.SetBounds(2, 6.07, 9); Assert.Equal(6.07, s.Snap(6.06, .1)!.Seconds);
        using var old = new Files(); var fallback = await old.Open(); fallback.SetBounds(1, 1.5, 4);
        Assert.Equal(PersonClipSnapKind.DetectionSegment, fallback.Snap(1.02, .1, 1)!.Kind);
        Assert.Equal("检测片段边界", fallback.Snap(1.02, .1, 1)!.Label);
    }

    [Fact]
    public async Task Evidence_reports_segment_statistics_and_two_second_context()
    {
        var samples = Scores().Select(sample => sample.Seconds == 2 ? sample with { Flags = sample.Flags | PersonClipSampleFlags.Boundary | PersonClipSampleFlags.Uncertain } : sample).ToArray();
        using var files = new Files(samples); var s = await files.Open(); var evidence = s.Evidence(1)!;
        Assert.Equal(6, evidence.Samples); Assert.Equal(6, evidence.PersonSamples); Assert.Equal(1, evidence.UncertainSamples); Assert.Equal(1, evidence.BoundarySamples);
        Assert.InRange(evidence.Average, .799, .801); Assert.InRange(evidence.Peak, .799, .801);
        Assert.Equal(0, evidence.Around[0].Seconds); Assert.Equal(6, evidence.Around[^1].Seconds);
        s.Add(16, 17); Assert.Equal(2, s.Evidence(s.Segments.Last().Id)!.Samples);
    }

    [Fact]
    public async Task Exclusions_trim_split_keep_fully_covered_entries_and_restore_in_one_step()
    {
        using var files = new Files(); var s = await files.Open();
        var before = State(s); s.AddExclusion(2, 3);
        Assert.Equal(4, s.Segments.Count); Assert.Equal((1d, 2d), (s.Segments[0].Start, s.Segments[0].End));
        Assert.Equal((3d, 4d), (s.Segments[1].Start, s.Segments[1].End));
        s.Undo(); Assert.Equal(before, State(s)); s.Redo();
        s.AddExclusion(6, 9); Assert.False(s.Segments.Single(segment => segment.Id == 2).Keep);
        Assert.Equal(4, s.Segments.Count); s.RemoveExclusion(1); Assert.False(s.Segments.Single(segment => segment.Id == 2).Keep);
        s.RestoreDetection(); s.AddExclusion(0, 2); Assert.Equal(2, s.Segments[0].Start);
        s.AddExclusion(3, 5); Assert.Equal(3, s.Segments[0].End);
        s.AddExclusion(1, 4); Assert.False(s.Segments[0].Keep); // Fully covered trimmed entry is recoverable.
        s.RestoreSegment(1); Assert.Equal((1d, 4d, true), (s.Segments[0].Start, s.Segments[0].End, s.Segments[0].Keep));
        s.RestoreDetection(); s.AddExclusion(0, 20); Assert.Equal(0, s.KeptCount); Assert.Equal(3, s.Segments.Count);
        Assert.Throws<ArgumentException>(() => s.ToClipEditResult()); s.Undo(); Assert.Equal(3, s.KeptCount);
    }

    [Fact]
    public async Task Save_and_reopen_preserve_originals_state_order_effects_and_removed_entries()
    {
        using var files = new Files(Scores()); var s = await files.Open();
        s.SetKeep(2, false); s.Split(1, 2.5); var manual = s.Add(15, 17); s.SetEffects(manual, new() { Speed = 1.25, Flip = true, FadeIn = .3 });
        s.Move(manual, -1); s.AddExclusion(18, 19); var before = State(s); var original = files.Job.PersonDetectionResult;
        var changed = 0; files.Job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Job.PersonDetectionResult)) changed++; };
        await s.SaveAsync(); Assert.Equal(1, changed); Assert.NotSame(original, files.Job.PersonDetectionResult);
        Assert.NotNull(files.Job.PersonDetectionResult!.Edits);
        Assert.Equal(s.Segments.Where(segment => segment.Keep).Select(segment => segment.Start), files.Job.PersonDetectionResult.Result.Segments.Select(segment => segment.Start));
        files.Job.PersonDetectionResult = null; var reopened = await files.Open();
        Assert.Equal(before, State(reopened)); Assert.False(reopened.CanUndo); Assert.False(reopened.CanRedo);
        Assert.Equal(8, reopened.DetectedSeconds); Assert.False(reopened.Segments.Single(segment => segment.Id == 2).Keep);
        reopened.RestoreSegment(reopened.Segments.Single(segment => segment.Start == 2.5).Id);
        Assert.Equal(4, reopened.Segments.Single(segment => segment.Id == 1).End);
        reopened.RestoreDetection(); Assert.Equal(0, reopened.ModifiedCount); Assert.Equal(3, reopened.KeptCount); Assert.Empty(reopened.Exclusions);
        Assert.Equal(4, reopened.Segments[0].End);
        // Ordinary jobs save their editing draft beside the internal people result, never into the video output.
        files.Job.Options.PersonClip = files.Job.Options.PersonClip! with { AnalysisOnly = false };
        files.Job.Output = Path.Combine(files.Root, "output.mp4"); files.Job.PersonDetectionResult = files.Detection;
        var other = await files.Open(); other.SetKeep(1, false);
        var path = AiTaskResults.PathFor(files.Job, "people");
        try
        {
            await other.SaveAsync(); Assert.True(File.Exists(path)); Assert.False(File.Exists(files.Job.Output));
            files.Job.PersonDetectionResult = null; Assert.Equal(2, (await files.Open()).KeptCount);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task Export_uses_output_order_and_individual_effects_through_joined_workflow()
    {
        using var files = new Files(); var s = await files.Open();
        var effects = new ConversionOptions { Speed = 1.5, FadeIn = .2, FadeOut = .3, AudioFadeIn = .1, AudioFadeOut = .15,
            CropX = 2, CropY = 2, CropWidth = 320, CropHeight = 180, Rotation = 90, Flip = true,
            DelogoRegions = [new(10, 10, 20, 20)], Volume = .7, VideoStreamIndex = 0, AudioStreamIndex = 0 };
        s.SetEffects(1, effects); s.Move(3, -1); s.SetKeep(2, false);
        var edit = s.ToClipEditResult(); Assert.Equal(new[] { 1d, 12d }, edit.Segments.Select(segment => segment.Start));
        var first = edit.Segments[0]; Assert.Equal(1.5, first.Speed); Assert.Equal(.2, first.FadeIn); Assert.Equal(.3, first.FadeOut);
        Assert.True(first.Flip); Assert.Equal(90, first.Rotation); Assert.Equal(320, first.CropWidth); Assert.Single(first.DelogoRegions!);
        QuickClipWorkflow.ValidateJoinedExports([edit], "MP4");
        var job = Assert.Single(QuickClipWorkflow.PrepareJoinedJobs([edit], "MP4", new() { VideoCodec = "libx264", AudioCodec = "aac" }, files.Root, false, "People"));
        Assert.NotNull(job.InputOptions); Assert.Equal(1.5, job.InputOptions![0].Speed); Assert.Equal(1, job.InputOptions[1].Speed);
        Assert.Equal(.1, job.InputOptions[0].AudioFadeIn); Assert.Equal(90, job.InputOptions[0].Rotation); Assert.Single(job.InputOptions[0].DelogoRegions!);
        Assert.Equal(new[] { 1d, 12d }, job.InputOptions.Select(option => option.Start));
        effects.Speed = 8; edit.Segments[0].Speed = 9;
        Assert.Equal(1.5, s.Segments[0].Options.Speed); // No mutable options leak into the session.
    }

    [Fact]
    public async Task Source_changes_block_export_but_allow_saving_and_editing()
    {
        using var files = new Files(); var s = await files.Open(); Assert.False(s.SourceChanged);
        var stamp = File.GetLastWriteTimeUtc(s.SourcePath);
        File.SetLastWriteTimeUtc(s.SourcePath, stamp.AddSeconds(2)); Assert.True(s.SourceChanged);
        File.SetLastWriteTimeUtc(s.SourcePath, stamp); Assert.False(s.SourceChanged);
        await File.AppendAllTextAsync(s.SourcePath, "changed"); Assert.True(s.SourceChanged);
        s.SetBounds(1, 1.5, 4); await s.SaveAsync(); Assert.Throws<ArgumentException>(() => s.ToClipEditResult());
        files.Job.PersonDetectionResult = null; Assert.True((await files.Open()).SourceChanged);
        File.Delete(s.SourcePath); Assert.True(s.SourceChanged);
    }

    [Fact]
    public async Task Missing_or_partial_results_are_rejected_with_Chinese_messages_and_cancellation_is_respected()
    {
        using var files = new Files(); files.Job.PersonDetectionResult = null;
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(files.Open); Assert.Contains("完整", missing.Message);
        files.Job.PersonDetectionResult = files.Detection with { Result = files.Detection.Result with { IsPartial = true } };
        Assert.Contains("完整", (await Assert.ThrowsAsync<InvalidOperationException>(files.Open)).Message);
        files.Job.PersonDetectionResult = null; await File.WriteAllTextAsync(files.Job.Output, "{");
        Assert.Contains("读取", (await Assert.ThrowsAsync<InvalidOperationException>(files.Open)).Message);
        files.Job.PersonDetectionResult = files.Detection; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PersonClipEditing.OpenAsync(files.Job, canceled.Token));
        var s = await files.Open(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.SaveAsync(canceled.Token));
        Assert.Same(files.Detection, files.Job.PersonDetectionResult);
    }

    [Fact]
    public async Task Redetect_options_preserve_detector_choices_and_normalize_session_exclusions()
    {
        var detection = new PersonClipOptions(DetectorIds: [ModelCatalog.PersonId], DetectionMode: PersonDetectionMode.Recall,
            IncludeHands: true, ExcludedRanges: [new(16, 17)], Threshold: .5);
        using var files = new Files(options: detection); var s = await files.Open(); Assert.Equal(0, s.ModifiedCount);
        s.AddExclusion(17, 18); Assert.Equal(1, s.ModifiedCount); var options = s.RedetectOptions();
        Assert.Equal(detection.DetectorIds, options.DetectorIds); Assert.Equal(.5, options.Threshold); Assert.True(options.IncludeHands);
        Assert.Equal(PersonDetectionMode.Recall, options.DetectionMode); Assert.Equal(new[] { new PersonClipRange(16, 18) }, options.ExcludedRanges);
        Assert.Equal(new[] { new PersonClipRange(16, 17) }, detection.ExcludedRanges);
        s.RestoreDetection(); Assert.Equal(0, s.ModifiedCount); Assert.Equal(detection.ExcludedRanges, s.Exclusions);
    }
}
