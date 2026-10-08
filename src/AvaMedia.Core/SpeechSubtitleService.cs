using System.Globalization;
using System.Text;
using Whisper.net;

namespace AvaMedia.Core;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

public sealed class SpeechSubtitleService(IMediaEngine engine, SpeechModelInstaller? installer = null)
{
    private static readonly SemaphoreSlim RecognitionGate = new(1, 1);
    private const double ChunkSeconds = 300;

    public static void Validate(Job job)
    {
        if (job.Inputs.Length != 1) throw new ArgumentException("每个字幕任务处理一个文件。");
        if (job.Options.Format is not ("mp4" or "mkv" or "srt" or "ass")) throw new ArgumentException("自动字幕支持 MP4、MKV、SRT 和 ASS。");
        (job.Options.Transcription ?? new()).Validate();
        var style = job.Options.Clone();
        style.SubtitleMode = SubtitleMode.None; style.Subtitle = "";
        MediaEngine.ValidateEncodingOptions(style);
    }

    public async Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(Job source, TranscriptionOptions speech, int audioTrack,
        Action<double> progress, CancellationToken ct, Action<AiActivity>? activityProgress = null)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "AvaMedia-transcript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var task = new Job { FeatureId = "auto-subtitle", Inputs = source.Inputs,
            Output = Path.Combine(temporary, "subtitles.srt"), Options = new() { Format = "srt", Transcription = speech.Clone(), AudioStreamIndex = audioTrack } };
        task.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(Job.Activity) && task.Activity is { } activity)
            {
                if (activityProgress is not null) activityProgress(activity);
                else source.Activity = activity;
            }
            if (change.PropertyName == nameof(Job.ProgressDetail)) source.ProgressDetail = task.ProgressDetail;
        };
        try
        {
            await ExecuteAsync(task, progress, ct, allowEmpty: true).ConfigureAwait(false);
            return SubtitleTranscript.Parse(await File.ReadAllTextAsync(task.Output, ct).ConfigureAwait(false));
        }
        finally { Directory.Delete(temporary, true); }
    }

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct, bool allowEmpty = false)
    {
        var options = job.Options;
        var speech = options.Transcription ?? new();
        var activity = new AiActivityReporter(value => job.Activity = value, "Whisper " + speech.Model, "条字幕",
            ["读取音轨", "语音模型", "语音识别", "保存结果"]);
        activity.Stage("读取音轨");
        var info = await engine.Probe(job.Inputs[0], ct, options.VideoStreamIndex, options.AudioStreamIndex).ConfigureAwait(false);
        if (!info.HasAudio || info.Duration <= 0) throw new ArgumentException("文件没有可识别的音轨或有效时长。");
        if (options.Format is "mp4" or "mkv" && !info.HasVideo) throw new ArgumentException("音频文件请选择 SRT 或 ASS 字幕输出。");
        if (options.Format is "mp4" or "mkv")
        {
            var filters = await ProcessRunner.Run(engine.FFmpeg, ["-hide_banner", "-filters"], ct).ConfigureAwait(false);
            if (filters.ExitCode != 0 || !System.Text.RegularExpressions.Regex.IsMatch(filters.Output + filters.Error, @"\bsubtitles\b"))
                throw new InvalidOperationException("当前 FFmpeg 不支持字幕烧录，请使用应用内置版本。");
        }
        var duration = Math.Min(options.End > 0 ? options.End : info.Duration, info.Duration) - options.Start;
        if (duration <= 0) throw new ArgumentException("识别区间超出源文件时长。");
        job.Duration = duration / options.Speed;
        job.ProgressDetail = "准备语音模型"; progress(0);
        activity.Node("语音模型");
        activity.Stage("准备语音模型");
        var models = installer ?? new();
        var model = await models.EnsureInstalledAsync(speech.Model, value =>
        {
            if (value.Stage == "下载") { activity.Stage("下载语音模型", value.Received, value.Total, "字节"); progress(value.Percent * .15); }
            else activity.Stage(value.Stage == "完成" ? "语音模型已就绪" : value.Stage);
        }, ct).ConfigureAwait(false);
        progress(15);
        var temporary = Path.Combine(Path.GetTempPath(), "AvaMedia-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!, ".AvaMedia-subtitles-" + Guid.NewGuid().ToString("N") + "." + options.Format);
        try
        {
            job.ProgressDetail = "等待语音识别";
            activity.Stage("等待语音识别", detail: "前一个识别任务完成后开始");
            await RecognitionGate.WaitAsync(ct).ConfigureAwait(false);
            List<SubtitleCue> cues;
            try
            {
                using var lease = await models.AcquireAsync(speech.Model, ct).ConfigureAwait(false);
                // Native inference stays off the UI thread; one model at a time bounds queue memory.
                cues = await Task.Run(async () =>
                {
                    activity.Stage("加载语音模型");
                    using var factory = WhisperFactory.FromPath(model);
                    using var vadFactory = WhisperVadFactory.FromPath(SpeechAssets.EnsureVadModel());
                    using var vad = vadFactory.CreateBuilder().WithUseGpu(false)
                        .WithThreads(engine.Settings.MultiThread ? Math.Clamp(engine.Settings.CpuThreads, 1, 4) : 1).WithThreshold(.5f)
                        .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250)).WithMinSilenceDuration(TimeSpan.FromMilliseconds(150))
                        .WithSpeechPadding(TimeSpan.FromMilliseconds(100)).Build();
                    activity.Node("语音识别");
                    double chunkBegin = 0, chunkEnd = 0, recognized = 0;
                    var recognitionProgressGate = new object();
                    void Recognized(double seconds)
                    {
                        lock (recognitionProgressGate)
                        {
                            recognized = Math.Max(recognized, Math.Min(duration, seconds));
                            activity.Advance(recognized, duration, "秒");
                            progress(15 + 60 * recognized / duration);
                        }
                    }
                    var builder = factory.CreateBuilder().WithLanguage(speech.Language).WithNoContext()
                        .WithProgressHandler(percent => Recognized(chunkBegin + (chunkEnd - chunkBegin) * Math.Clamp(percent, 0, 100) / 100d))
                        .WithThreads(Math.Clamp(engine.Settings.MultiThread ? engine.Settings.CpuThreads : 1, 1, 8))
                        .WithNoSpeechThreshold(.6f).WithTokenTimestamps().WithMaxSegmentLength(42);
                    if (speech.Language == "zh") builder.WithPrompt("以下是简体中文普通话的转录。");
                    using var processor = builder.Build();
                    var result = new List<SubtitleCue>();
                    var wav = Path.Combine(temporary, "speech.wav");
                    var regionWav = Path.Combine(temporary, "speech-region.wav");
                    for (var from = 0d; from < duration; from += ChunkSeconds)
                    {
                        ct.ThrowIfCancellationRequested();
                        var begin = Math.Max(0, from - 1);
                        var end = Math.Min(duration, from + ChunkSeconds + 1);
                        job.ProgressDetail = "提取音轨";
                        activity.Stage("提取音轨", detail: $"第 {(int)(from / ChunkSeconds) + 1} / {(int)Math.Ceiling(duration / ChunkSeconds)} 段");
                        var extracted = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-nostdin", "-y", "-ss", MediaEngine.Number(options.Start + begin),
                            "-i", job.Inputs[0], "-map", $"0:a:{options.AudioStreamIndex}", "-t", MediaEngine.Number(end - begin),
                            "-vn", "-sn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav], ct).ConfigureAwait(false);
                        if (extracted.ExitCode != 0) throw new InvalidDataException("提取识别音轨失败。\n" + extracted.Error);
                        job.ProgressDetail = "检测语音";
                        activity.Stage("检测语音", detail: $"{MediaTime.Format(begin)} – {MediaTime.Format(end)}");
                        IReadOnlyList<VadSegmentData> speechSegments;
                        await using (var detectionAudio = File.OpenRead(wav))
                            speechSegments = await vad.DetectSpeechAsync(detectionAudio, ct).ConfigureAwait(false);
                        if (speechSegments.Count == 0)
                        {
                            activity.Result($"{MediaTime.Format(begin)} – {MediaTime.Format(end)} · 未检测到语音", result.Count);
                            Recognized(Math.Min(duration, from + ChunkSeconds)); continue;
                        }
                        job.ProgressDetail = "识别字幕";
                        activity.Stage("识别字幕", recognized, duration, "秒", $"检测到 {speechSegments.Count} 个语音区间");
                        // Decode the detected utterances individually. A native
                        // 30-second decoding window can otherwise skip later speech
                        // after an early end-of-text prediction, despite VAD coverage.
                        foreach (var region in speechSegments)
                        {
                            var regionStart = Math.Max(0, region.Start.TotalSeconds);
                            var regionStop = Math.Min(end - begin, region.End.TotalSeconds);
                            if (regionStop <= regionStart) continue;
                            var prepared = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-nostdin", "-y", "-ss", MediaEngine.Number(regionStart),
                                "-i", wav, "-t", MediaEngine.Number(regionStop - regionStart), "-c:a", "pcm_s16le", regionWav], ct).ConfigureAwait(false);
                            if (prepared.ExitCode != 0) throw new InvalidDataException("提取语音区间失败。\n" + prepared.Error);
                            chunkBegin = begin + regionStart; chunkEnd = begin + regionStop;
                            await using var audio = File.OpenRead(regionWav);
                            await foreach (var segment in processor.ProcessAsync(audio, ct).ConfigureAwait(false))
                            {
                                var text = segment.Text.Trim();
                                var start = Math.Max(chunkBegin, chunkBegin + segment.Start.TotalSeconds);
                                var stop = Math.Min(chunkEnd, chunkBegin + segment.End.TotalSeconds);
                                var middle = (start + stop) / 2;
                                // Overlap protects words at chunk edges; each segment belongs to one chunk.
                                if (!text.Any(char.IsLetterOrDigit) || stop <= start || middle < from || middle >= Math.Min(duration, from + ChunkSeconds)) continue;
                                // Whisper can confidently invent text for non-speech. Its timestamps include pauses,
                                // so require some actual speech support without cutting words to the detector's edges.
                                var supported = speechSegments.Sum(segment => Math.Max(0,
                                    Math.Min(stop, begin + segment.End.TotalSeconds) - Math.Max(start, begin + segment.Start.TotalSeconds)));
                                if (supported < Math.Min(.25, stop - start) || supported < (stop - start) * .2) continue;
                                if (result.LastOrDefault() is { } previous)
                                {
                                    if (previous.Text == text && start / options.Speed < previous.End.TotalSeconds + .5) continue;
                                    start = Math.Max(start, previous.End.TotalSeconds * options.Speed);
                                }
                                if (stop <= start) continue;
                                result.Add(new(TimeSpan.FromSeconds(start / options.Speed), TimeSpan.FromSeconds(stop / options.Speed), text));
                                activity.Result($"{MediaTime.Format(start / options.Speed)} – {MediaTime.Format(stop / options.Speed)}  {text}", result.Count);
                                Recognized(stop);
                            }
                        }
                        Recognized(Math.Min(duration, from + ChunkSeconds));
                    }
                    return result;
                }, ct).ConfigureAwait(false);
            }
            finally { RecognitionGate.Release(); }
            if (cues.Count == 0 && !allowEmpty) throw new InvalidDataException("未识别到语音，请检查音轨或更换识别语言。");
            activity.Node("保存结果");
            if (options.Format is "srt" or "ass")
            {
                job.ProgressDetail = "保存字幕";
                activity.Stage("保存字幕");
                await File.WriteAllTextAsync(output, options.Format == "srt" ? SpeechSubtitles.Srt(cues) : SpeechSubtitles.Ass(cues, options, info.Width, info.Height), new UTF8Encoding(false), ct).ConfigureAwait(false);
            }
            else
            {
                var ass = Path.Combine(temporary, "subtitles.ass");
                var rendered = options.Clone(); rendered.Transcription = null;
                rendered.SubtitleMode = SubtitleMode.BurnIn; rendered.Subtitle = ass; rendered.SubtitleStreamIndex = 0;
                // Cues already use the output clock. Keep original-time timestamps for the existing seek/speed filter.
                var originalCues = cues.Select(cue => new SubtitleCue(TimeSpan.FromSeconds(cue.Start.TotalSeconds * options.Speed + options.Start),
                    TimeSpan.FromSeconds(cue.End.TotalSeconds * options.Speed + options.Start), cue.Text)).ToArray();
                await File.WriteAllTextAsync(ass, SpeechSubtitles.Ass(originalCues, options, info.Width, info.Height), new UTF8Encoding(false), ct).ConfigureAwait(false);
                rendered.CopyStreams = false; if (rendered.VideoCodec == "copy") rendered.VideoCodec = "自动";
                var conversion = new Job { FeatureId = "mp4", Inputs = job.Inputs, Output = output, Options = rendered };
                job.ProgressDetail = "写入视频字幕";
                activity.Stage("写入视频字幕", 0, 100, "%");
                await engine.Execute(conversion, percent => { activity.Advance(percent, 100, "%"); progress(75 + percent * .25); }, ct).ConfigureAwait(false);
                job.Log = conversion.Log;
            }
            ct.ThrowIfCancellationRequested();
            File.Move(output, job.Output);
            job.ProgressDetail = "字幕已生成";
            job.Log += $"\n{speech.Model} · {speech.Language} · {cues.Count} subtitles";
            progress(100);
            activity.Finish("字幕已生成");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
            Directory.Delete(temporary, true);
        }
    }
}

public static class SpeechSubtitles
{
    public static string Srt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder(); int index = 0;
        foreach (var cue in cues)
            builder.Append(++index).Append('\n').Append(Time(cue.Start, false)).Append(" --> ").Append(Time(cue.End, false))
                .Append('\n').Append(cue.Text.Replace("\r", "")).Append("\n\n");
        return builder.ToString();
    }

    public static string Ass(IEnumerable<SubtitleCue> cues, ConversionOptions style, int width = 1920, int height = 1080)
    {
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;
        var rgb = style.SubtitleColor[1..];
        var color = "&H00" + rgb[4..6] + rgb[2..4] + rgb[..2];
        var font = style.SubtitleFont.Length > 0 ? style.SubtitleFont : OperatingSystem.IsMacOS() ? "PingFang SC" : "Microsoft YaHei";
        var size = style.SubtitleFontSize > 0 ? style.SubtitleFontSize : 48;
        var builder = new StringBuilder(FormattableString.Invariant($"[Script Info]\nScriptType: v4.00+\nPlayResX: {Math.Max(1, width)}\nPlayResY: {Math.Max(1, height)}\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,{font},{size},{color},&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,0,{style.SubtitleAlignment},{style.SubtitleMargin},{style.SubtitleMargin},{style.SubtitleMargin},1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"));
        foreach (var cue in cues)
        {
            var text = cue.Text.Replace("\\", "＼").Replace("{", "｛").Replace("}", "｝").Replace("\r", "").Replace("\n", "\\N");
            builder.Append("Dialogue: 0,").Append(Time(cue.Start, true)).Append(',').Append(Time(cue.End, true))
                .Append(",Default,,0,0,0,,").Append(SubtitlePositioning.Tag(style, width, height)).Append(text).Append('\n');
        }
        return builder.ToString();
    }

    private static string Time(TimeSpan value, bool ass) => ass
        ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", (long)value.TotalHours, value.Minutes, value.Seconds, value.Milliseconds / 10)
        : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", (long)value.TotalHours, value.Minutes, value.Seconds, value.Milliseconds);
}
