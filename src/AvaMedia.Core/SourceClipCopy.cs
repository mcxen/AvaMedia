using System.Text;
using System.Globalization;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Remux retained ranges from one source and concatenate their original encoded streams.</summary>
public static class SourceClipCopy
{
    public static bool IsJoined(Job job) => job.FeatureId == "join" && job.Options.CopyStreams && job.InputOptions is not null;

    public static void Validate(Job job)
    {
        if (!IsJoined(job)) return;
        var options = job.Options;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (job.InputOptions!.Count != job.Inputs.Length || job.Inputs.Any(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(job.Inputs[0]), comparison)))
            throw new ArgumentException("原格式合并仅支持同一源视频的片段。");
        if (options.Start != 0 || options.End != 0 || MediaEngine.HasFilters(options) || options.SampleRate > 0 || options.AudioChannels > 0)
            throw new ArgumentException("原格式合并只连接保留片段；应用画面或音频效果请选择重新编码格式。");
        foreach (var part in job.InputOptions)
            if (!part.CopyStreams || part.Format != options.Format || part.VideoStreamIndex != options.VideoStreamIndex ||
                part.AudioStreamIndex != options.AudioStreamIndex || part.Mute != options.Mute || part.KeepAllAudioStreams != options.KeepAllAudioStreams ||
                part.KeepMetadata != options.KeepMetadata || SubtitleOptions.Mode(part) != SubtitleOptions.Mode(options) ||
                part.SubtitleStreamIndex != options.SubtitleStreamIndex || part.Subtitle != options.Subtitle)
                throw new ArgumentException("原格式合并的各片段必须使用相同的源格式和轨道设置。");
        if (options.Format != Path.GetExtension(job.Inputs[0]).TrimStart('.').ToLowerInvariant())
            throw new ArgumentException("原格式合并必须保留源视频的容器格式。");
    }

    public static async Task ExecuteAsync(MediaEngine engine, Job job, IReadOnlyList<MediaInfo> infos, Action<double> progress, CancellationToken ct)
    {
        Validate(job);
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!, ".AvaMedia-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            job.ProgressDetail = "原格式 / 原码率 · 流复制";
            var timings = new List<(string Name, double Offset, double Duration)>();
            for (var index = 0; index < job.Inputs.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var name = $"part-{index:000}.{job.Options.Format}";
                var part = new Job { FeatureId = "clip", Inputs = [job.Inputs[index]], Output = Path.Combine(folder, name), Options = job.InputOptions![index].Clone() };
                part.Duration = MediaEngine.ValidateEdits(part, [infos[index]]);
                var current = index;
                await Run(MediaEngine.BuildArguments(part, [infos[index]]), seconds =>
                    progress(75 * (current + Math.Clamp(seconds / part.Duration, 0, 1)) / job.Inputs.Length));
                var info = await engine.Probe(part.Output, ct);
                using var json = JsonDocument.Parse(info.RawJson);
                var origin = Seconds(json.RootElement.GetProperty("format"), "start_time");
                double first = double.PositiveInfinity, end = double.NegativeInfinity;
                // Stream duration is based on DTS and can omit a reordered terminal B-frame.
                // Inspect actual presentation timestamps so a following range cannot overlap it.
                var inspected = await ProcessRunner.Run(engine.FFprobe,
                    ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", part.Output], ct, line =>
                    {
                        var values = line.Split(',');
                        if (!double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) || !double.IsFinite(time)) return;
                        var length = values.Length > 1 && double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0
                            ? value : info.FrameRate > 0 ? 1 / info.FrameRate : 0;
                        first = Math.Min(first, time); end = Math.Max(end, time + length);
                    }, maximumOutputChars: 4096);
                if (inspected.ExitCode != 0 || !double.IsFinite(first) || !double.IsFinite(end) || end <= first)
                    throw new InvalidDataException("无法读取原编码片段的时间边界。" + inspected.Error);
                timings.Add((name, first - origin, end - first));
            }
            // Concat otherwise uses each container's duration and differing audio preroll.
            // Align the next video's first frame with this video's last frame boundary.
            var list = new StringBuilder("ffconcat version 1.0\n");
            for (var index = 0; index < timings.Count; index++)
            {
                var part = timings[index];
                var duration = part.Duration + part.Offset - (index + 1 < timings.Count ? timings[index + 1].Offset : 0);
                if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("片段过短，无法按原始编码连接，请选择重新编码格式。");
                list.Append("file '").Append(part.Name).Append("'\nduration ").Append(MediaEngine.Number(duration)).Append('\n');
            }
            job.Duration = timings.Sum(part => part.Duration);
            var manifest = Path.Combine(folder, "parts.ffconcat");
            await File.WriteAllTextAsync(manifest, list.ToString(), new UTF8Encoding(false), ct);
            // Every range comes from the same source. Keep the source packet representation
            // instead of concat's automatic H.264 Annex B conversion and repeated SPS/PPS.
            List<string> arguments = ["-hide_banner", "-nostdin", "-n", "-progress", "pipe:1", "-nostats", "-f", "concat", "-safe", "0", "-auto_convert", "0", "-i", manifest,
                "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero"];
            if (!job.Options.KeepMetadata) arguments.AddRange(["-map_metadata", "-1"]);
            VideoFormats.AppendMuxerArguments(arguments, job.Options.Format);
            var joined=Path.Combine(folder,"joined."+job.Options.Format);
            arguments.Add(joined);
            await Run(arguments, seconds => progress(75 + 24.9 * Math.Clamp(seconds / job.Duration, 0, 1)));
            ct.ThrowIfCancellationRequested();File.Move(joined,job.Output);
            progress(100);
        }
        finally { Directory.Delete(folder, true); }

        async Task Run(List<string> arguments, Action<double> advance)
        {
            var result = await ProcessRunner.Run(engine.FFmpeg, arguments, ct, line =>
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var microseconds)) advance(microseconds / 1_000_000d);
            });
            job.AppendLog(result.Output + result.Error);
            if (result.ExitCode != 0) throw new InvalidDataException(result.Error);
        }
    }

    private static double Seconds(JsonElement item, string property, double fallback = 0) =>
        item.TryGetProperty(property, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) ? seconds : fallback;
}
