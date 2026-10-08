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

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        var options = job.Options;
        var speech = options.Transcription ?? new();
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
        var models = installer ?? new();
        var model = await models.EnsureInstalledAsync(speech.Model, percent =>
        { job.ProgressDetail = "准备语音模型"; progress(percent * .15); }, ct).ConfigureAwait(false);
        var temporary = Path.Combine(Path.GetTempPath(), "AvaMedia-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!, ".AvaMedia-subtitles-" + Guid.NewGuid().ToString("N") + "." + options.Format);
        try
        {
            job.ProgressDetail = "等待语音识别";
            await RecognitionGate.WaitAsync(ct).ConfigureAwait(false);
            List<SubtitleCue> cues;
            try
            {
                using var lease = await models.AcquireAsync(speech.Model, ct).ConfigureAwait(false);
                // Native inference stays off the UI thread; one model at a time bounds queue memory.
                cues = await Task.Run(async () =>
                {
                    using var factory = WhisperFactory.FromPath(model);
                    var builder = factory.CreateBuilder().WithLanguage(speech.Language).WithNoContext()
                        .WithThreads(Math.Clamp(engine.Settings.MultiThread ? engine.Settings.CpuThreads : 1, 1, 8))
                        .WithNoSpeechThreshold(.6f).WithTokenTimestamps().WithMaxSegmentLength(42);
                    if (speech.Language == "zh") builder.WithPrompt("以下是简体中文普通话的转录。");
                    using var processor = builder.Build();
                    var result = new List<SubtitleCue>();
                    var wav = Path.Combine(temporary, "speech.wav");
                    for (var from = 0d; from < duration; from += ChunkSeconds)
                    {
                        ct.ThrowIfCancellationRequested();
                        var begin = Math.Max(0, from - 1);
                        var end = Math.Min(duration, from + ChunkSeconds + 1);
                        job.ProgressDetail = "提取音轨";
                        var extracted = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-nostdin", "-y", "-ss", MediaEngine.Number(options.Start + begin),
                            "-i", job.Inputs[0], "-map", $"0:a:{options.AudioStreamIndex}", "-t", MediaEngine.Number(end - begin),
                            "-vn", "-sn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav], ct).ConfigureAwait(false);
                        if (extracted.ExitCode != 0) throw new InvalidDataException("提取识别音轨失败。\n" + extracted.Error);
                        job.ProgressDetail = "识别字幕";
                        await using var audio = File.OpenRead(wav);
                        await foreach (var segment in processor.ProcessAsync(audio, ct).ConfigureAwait(false))
                        {
                            var text = segment.Text.Trim();
                            var start = Math.Max(0, begin + segment.Start.TotalSeconds);
                            var stop = Math.Min(duration, begin + segment.End.TotalSeconds);
                            var middle = (start + stop) / 2;
                            // Overlap protects words at chunk edges; each segment belongs to one chunk.
                            if (text.Length == 0 || stop <= start || middle < from || middle >= Math.Min(duration, from + ChunkSeconds)) continue;
                            if (result.LastOrDefault() is { } previous)
                            {
                                if (previous.Text == text && start / options.Speed < previous.End.TotalSeconds + .5) continue;
                                start = Math.Max(start, previous.End.TotalSeconds * options.Speed);
                            }
                            if (stop <= start) continue;
                            result.Add(new(TimeSpan.FromSeconds(start / options.Speed), TimeSpan.FromSeconds(stop / options.Speed), text));
                            progress(15 + 60 * Math.Min(duration, stop) / duration);
                        }
                        progress(15 + 60 * Math.Min(duration, from + ChunkSeconds) / duration);
                    }
                    return result;
                }, ct).ConfigureAwait(false);
            }
            finally { RecognitionGate.Release(); }
            if (cues.Count == 0) throw new InvalidDataException("未识别到语音，请检查音轨或更换识别语言。");
            if (options.Format is "srt" or "ass")
            {
                job.ProgressDetail = "保存字幕";
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
                await engine.Execute(conversion, percent => progress(75 + percent * .25), ct).ConfigureAwait(false);
                job.Log = conversion.Log;
            }
            ct.ThrowIfCancellationRequested();
            File.Move(output, job.Output);
            job.ProgressDetail = "字幕已生成";
            job.Log += $"\n{speech.Model} · {speech.Language} · {cues.Count} subtitles";
            progress(100);
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
                .Append(",Default,,0,0,0,,").Append(text).Append('\n');
        }
        return builder.ToString();
    }

    private static string Time(TimeSpan value, bool ass) => ass
        ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", (long)value.TotalHours, value.Minutes, value.Seconds, value.Milliseconds / 10)
        : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", (long)value.TotalHours, value.Minutes, value.Seconds, value.Milliseconds);
}
