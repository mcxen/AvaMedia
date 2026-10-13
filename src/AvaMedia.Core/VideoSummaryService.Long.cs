namespace AvaMedia.Core;

public sealed partial class VideoSummaryService
{
    private async Task<VideoSummaryResult> ExecuteLongAsync(Job job, MediaInfo info, Action<double> progress, CancellationToken ct)
    {
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("请选择有画面和有效时长的视频。");
        var options = job.Options.VideoSummary!; var folder = AiPartialResults.SummaryFolder(job);
        Directory.CreateDirectory(folder); job.SummaryReport = null; job.LongSummaryReport = null; job.SummarySegments = [];
        var ocr = ocrEngine ?? new NullOcrEngine();
        var store = new SummaryCheckpointStore(Path.Combine(folder, "work"));
        var file = new FileInfo(job.Inputs[0]);
        var sourceHash = SummaryCheckpointStore.Hash(new { file.FullName, file.Length, file.LastWriteTimeUtc, info.Duration, job.Options.VideoStreamIndex });
        await store.WriteAsync("probe.json", sourceHash, info, ct);
        var online = options.Provider == VideoSummaryProvider.Online ? ResolveOnlineModels(engine.Settings.OnlineAi, options) : null;
        var budget = online is null ? TokenBudget.Local8K : TokenBudget.For(online.SummaryProvider.ModelInfo.FirstOrDefault(m => m.Id == online.SummaryModel) ?? new(online.SummaryModel));
        var speech = options.Speech.Clone(); speech.Language = "auto";
        var external = options.TranscriptSource == VideoTranscriptSource.External ? new FileInfo(options.SubtitleFile) : null;
        var transcriptHash = SummaryCheckpointStore.Hash(new { sourceHash, options.TranscriptSource, options.SubtitleTrack, options.AudioTrack, options.AutoSelectAudioTrack,
            options.AllAudioTracks, speech.Model, options.OutputLanguage, transcriptPolicyVersion = 2, external = external is null ? null : new { external.FullName, external.Length, external.LastWriteTimeUtc } });
        var visionIdentity = online is null ? options.LocalVisionModelId : SummaryCheckpointStore.Hash(new { online.VisionProvider.Endpoint, online.VisionModel });
        var signalHash = SummaryCheckpointStore.Hash(new { transcriptHash, options.AnalyzeFrames, visionIdentity, ocr = ocr.GetType().FullName, ocrVersion = 1, options.FrameCount, budget.FrameLimit, agentVersion = VideoSummaryFrameAgent.Version });
        var activity = new AiActivityReporter(a => job.Activity = a, "视频信号", "段", ["提取信号"]);
        // Complete visual extraction before acquiring the summary lease, reusing both existing caches.
        var signals = await store.GetOrCreateAsync("signals.json", signalHash, Extract, ct);
        var subtitleCues = (signals.RawCues ?? signals.Cues).Select(c => new SubtitleCue(TimeSpan.FromSeconds(c.Start), TimeSpan.FromSeconds(c.End), c.Text)
            { Language = c.Language, Track = c.Track, LanguageConfidence = c.LanguageConfidence }).ToArray();
        job.SubtitleResult = new(subtitleCues, file.Length, file.LastWriteTimeUtc);
        if (options.ExtractSubtitles) await File.WriteAllTextAsync(Path.Combine(folder, "subtitles.srt"), SpeechSubtitles.Srt(subtitleCues), ct);
        await using var text = online is not null && modelFactory is null
            ? new OnlineSummaryModel(online.SummaryProvider, model: online.SummaryModel, boundedOutput: true)
            : await OpenLongModelAsync(ModelCatalog.SummaryTextId, online, options, activity, ct);
        var identity = online is null ? text.ModelId : SummaryCheckpointStore.Hash(new { online.SummaryProvider.Endpoint, online.SummaryModel });
        var model = new RetryingSummaryModel(new LongSummaryModel(text, identity));
        var gate = new ConcurrencyGate(identity, options.Provider == VideoSummaryProvider.Local, options.LongSummaryConcurrency, options.LongSummaryRequestsPerMinute, options.LongSummaryTokensPerMinute);
        var report = await new LongVideoSummaryOrchestrator(model, budget, gate, modelIdentity: identity).RunAsync(job, folder, signalHash, (_, _) => Task.FromResult(signals), progress, ct);
        if (Directory.Exists(job.Output)) throw new IOException("总结输出目录已存在，请选择新的输出位置。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.Output))!);
        CopyDirectory(folder, job.Output, ct);
        return new([], report.Summary, text.ModelId);

        async Task<SummarySignals> Extract(CancellationToken cancellation)
        {
            var transcript = await store.GetOrCreateAsync("asr/transcript.json", transcriptHash, async token =>
            {
                var longOptions = options.Clone(); longOptions.Speech.Language = "auto";
                var tracks = LongSummaryMediaPolicy.Tracks(info.RawJson, "audio");
                var preferred = options.Speech.Language == "auto" ? options.OutputLanguage switch { "English" => "en", "日本語" => "ja", _ => "zh" } : options.Speech.Language;
                if (longOptions.AutoSelectAudioTrack && tracks.Count > 0) longOptions.AudioTrack = LongSummaryMediaPolicy.SelectTrack(tracks, preferred);
                var extractionJob = new Job { Id = job.Id, Inputs = job.Inputs, Output = job.Output, Options = job.Options.Clone() }; extractionJob.Options.VideoSummary = longOptions;
                var (cues, transcriptSource) = await ReadTranscriptAsync(extractionJob, info, folder, v => progress(v * .25), activity, token, store);
                var voices = (AiProgressStore.GetSpeech(job.Id.ToString())?.VoiceIntervals ?? []).ToList();
                var all = cues.Select(c => new SummaryCue("source-" + c.Start.Ticks, c.Start.TotalSeconds, c.End.TotalSeconds, c.Text, Language: c.Language, Track: c.Track, LanguageConfidence: c.LanguageConfidence) { Source = transcriptSource }).ToList();
                if (options.AllAudioTracks && info.HasAudio && options.TranscriptSource is VideoTranscriptSource.Speech or VideoTranscriptSource.Automatic)
                    foreach (var track in tracks.Where(t => t.Index != longOptions.AudioTrack))
                    {
                        var extra = await new SpeechSubtitleService(engine).TranscribeAsync(job, longOptions.Speech, track.Index, _ => { }, token, activity.Observe, info, store);
                        voices.AddRange(AiProgressStore.GetSpeech(job.Id.ToString())?.VoiceIntervals ?? []);
                        all.AddRange(extra.Select(c => new SummaryCue("source-" + c.Start.Ticks + "-" + track.Index, c.Start.TotalSeconds, c.End.TotalSeconds, c.Text, Language: c.Language, Track: track.Index, LanguageConfidence: c.LanguageConfidence) { Source = "Whisper" }));
                    }
                var mergedVoices = new List<VoiceInterval>();
                foreach (var voice in voices.OrderBy(v => v.StartSeconds))
                {
                    if (mergedVoices.LastOrDefault() is { } previous && voice.StartSeconds <= previous.EndSeconds)
                        mergedVoices[^1] = previous with { EndSeconds = Math.Max(previous.EndSeconds, voice.EndSeconds) };
                    else mergedVoices.Add(voice);
                }
                return new TranscriptSignals(all.OrderBy(c => c.Start).ThenBy(c => c.Track).ToArray(), mergedVoices.ToArray(), transcriptSource);
            }, cancellation);
            var shots = options.AnalyzeFrames
                ? await store.GetOrCreateAsync("shots.json", sourceHash, async token => new ShotSignals((await ShotDetector.ScanAsync(engine, job.Inputs[0], info.Duration, token)).ToArray()), cancellation)
                : new ShotSignals(ShotDetector.FromBoundaries(info.Duration, []).ToArray());
            var rawCues = transcript.Cues;
            var eligible = new List<SummaryCue>();
            foreach (var group in rawCues.GroupBy(c => c.Source.Length > 0 ? c.Source : transcript.Source))
            {
                var selection = VideoSummaryTranscriptPolicy.Evaluate(group.Select(c => new SubtitleCue(TimeSpan.FromSeconds(c.Start), TimeSpan.FromSeconds(c.End), c.Text)
                    { Language = c.Language, Track = c.Track, LanguageConfidence = c.LanguageConfidence }).ToArray(), group.Key, info.Duration, transcript.Voices);
                var keys = selection.Cues.Select(c => (c.Start.TotalSeconds, c.End.TotalSeconds, c.Text, c.Track)).ToHashSet();
                eligible.AddRange(group.Where(c => keys.Contains((c.Start, c.End, c.Text, c.Track))));
                foreach (var note in selection.Notes) job.AppendLog(note);
            }
            var selectedCues = eligible.OrderBy(c => c.Start).ThenBy(c => c.Track).ToArray();
            var frames = new List<SummaryFrame>();
            var visualOnly = !info.HasAudio || selectedCues.Length == 0 || transcript.Voices.Length > 0 && transcript.Voices.Sum(v => v.EndSeconds - v.StartSeconds) / info.Duration < .03;
            if (options.AnalyzeFrames)
            {
                var visionId = online is null ? options.LocalVisionModelId : ModelCatalog.SummaryVisionId;
                await using var vision = await OpenLongModelAsync(visionId, online, options, activity, cancellation);
                var framePlan = new SemanticSegmenter().Plan(new(info.Duration, selectedCues, shots.Shots, transcript.Voices, []), budget, visualOnly: visualOnly);
                foreach (var range in framePlan.Segments)
                {
                    var hash = SummaryCheckpointStore.Hash(new { sourceHash, visionIdentity, range.Start, range.End,
                        transcript = SummaryCheckpointStore.Hash(selectedCues.Where(c => c.Start < range.End && c.End > range.Start)),
                        options.FrameCount, budget.FrameLimit, ocr = ocr.GetType().FullName, ocrVersion = 1, agentVersion = VideoSummaryFrameAgent.Version });
                    var investigation = await store.GetOrCreateAsync($"frames/{range.Id}-agent.json", hash, async token =>
                    {
                        var seeds = new[] { range.Start + (range.End - range.Start) * .15, range.Start + (range.End - range.Start) * .5,
                            range.Start + (range.End - range.Start) * .85 };
                        var cues = selectedCues.Where(c => c.Start < range.End && c.End > range.Start)
                            .Select(c => new SubtitleCue(TimeSpan.FromSeconds(c.Start), TimeSpan.FromSeconds(c.End), c.Text)
                                { Language = c.Language, Track = c.Track, LanguageConfidence = c.LanguageConfidence }).ToArray();
                        return await VideoSummaryFrameAgent.RunAsync(engine, job.Inputs[0], info.Duration, info.VideoStreamIndex,
                            range.Start, range.End, range.Id + "-", Math.Min(options.FrameCount, budget.FrameLimit), seeds, cues,
                            string.Join("、", selectedCues.Where(c => c.Start < range.End && c.End > range.Start).Select(c => c.Source).Distinct()), vision,
                            async (id, png, frameToken) =>
                            {
                                var relative = "work/frames/" + id + ".png";
                                await SummaryCheckpointStore.AtomicWriteAsync(Path.Combine(folder, relative), png, frameToken).ConfigureAwait(false);
                                activity.Frame(png, id); return relative;
                            }, detail => { job.ProgressDetail = detail; activity.Stage(detail); }, token,
                            value => store.WriteAsync($"frames/{range.Id}-agent-partial.json", hash, value, CancellationToken.None)).ConfigureAwait(false);
                    }, cancellation);
                    foreach (var observation in investigation.Observations)
                    {
                        var cited = investigation.Frames.Where(f => observation.FrameIds.Contains(f.Id)).OrderBy(f => f.Seconds).ToArray();
                        var frame = cited[0];
                        var shot = shots.Shots.FirstOrDefault(s => s.Start <= frame.Seconds && s.End > frame.Seconds);
                        var label = cited.Length == 1 ? "静态单帧观察" : observation.Sequence ? "近邻多帧观察" : "间隔采样的静态比较";
                        var caption = $"{label} [{MediaTime.Format(cited[0].Seconds)}–{MediaTime.Format(cited[^1].Seconds)}; frameIds={string.Join(",", observation.FrameIds)}]：{observation.Text}";
                        frames.Add(new(observation.Id, frame.Seconds, shot?.Id ?? "", caption,
                            ocr.Available && cited.Length == 1 ? await ocr.ReadAsync(await File.ReadAllBytesAsync(Path.Combine(folder, frame.Path), cancellation), cancellation) : "")
                            { Path = frame.Path, FrameIds = observation.FrameIds, End = cited[^1].Seconds, Kind = observation.Sequence ? "sequence" : cited.Length > 1 ? "comparison" : "frame" });
                    }
                    foreach (var note in investigation.Uncertainties) job.AppendLog(note);
                }
                if (visualOnly && !ocr.Available) job.AppendLog("无语音模式：只描述可见内容。当前未配置 OCR 引擎，标题文字覆盖可能不完整。");
            }
            return new SummarySignals(info.Duration, selectedCues, shots.Shots, transcript.Voices, frames, ocr.Available, info.HasAudio) { RawCues = rawCues };
        }
    }
    private async Task<ISummaryModel> OpenLongModelAsync(string id, VideoSummaryOnlineModels? online, VideoSummaryOptions options,
        AiActivityReporter activity, CancellationToken ct)
    {
        if (online is not null || modelFactory is not null) return await OpenModelAsync(id, online, options, activity, ct);
        await Task.WhenAll(EnsureModelAsync(ModelCatalog.SummaryRuntimeId, activity, ct), EnsureModelAsync(id, activity, ct));
        return await SharedSummaryModelLease.AcquireAsync(_models, id, ct, stage => activity.Stage(stage));
    }
    private sealed record TranscriptSignals(SummaryCue[] Cues, VoiceInterval[] Voices, string Source);
    private sealed record ShotSignals(SummaryShot[] Shots);
    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested(); var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, overwrite: false);
        }
    }
}
