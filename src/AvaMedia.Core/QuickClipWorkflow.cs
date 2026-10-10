namespace AvaMedia.Core;

public sealed record ClipEditResult(string Path, MediaInfo Info, IReadOnlyList<ConversionOptions> Segments, bool RemoveSelected = false)
{
    public IReadOnlyList<ConversionOptions> OutputSegments => QuickClipWorkflow.ResolveSegments(Segments, Info.Duration, RemoveSelected);
}

/// <summary>Export settings are applied without replacing any segment's editing draft.</summary>
public static class QuickClipWorkflow
{
    public static IReadOnlyList<ConversionOptions> ResolveSegments(IReadOnlyList<ConversionOptions> segments, double duration, bool removeSelected)
    {
        if (!removeSelected) return segments.Select(segment => segment.Clone()).ToArray();
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentException("请选择有效视频。");
        var exclusions = segments.Select(segment =>
        {
            var end = segment.End == 0 ? duration : segment.End;
            if (!double.IsFinite(segment.Start) || !double.IsFinite(end) || segment.Start < 0 || end <= segment.Start || end > duration || segment.Start >= duration)
                throw new ArgumentException("片段时间必须位于视频内，且结束时间晚于开始时间。");
            return new PersonClipRange(segment.Start, end);
        });
        // Subtract normalizes masks first: overlaps, nested ranges and touching boundaries form one union.
        var retained = PersonClipExclusions.Subtract([new(0, duration)], exclusions);
        var source = segments.FirstOrDefault();
        return retained.Select(range => new ConversionOptions
        {
            Start = range.Start, End = range.End,
            VideoStreamIndex = source?.VideoStreamIndex ?? 0,
            AudioStreamIndex = source?.AudioStreamIndex ?? 0,
            KeepAllAudioStreams = source?.KeepAllAudioStreams ?? false
        }).ToArray();
    }

    public static void ValidateJoinedExports(IEnumerable<ClipEditResult> edits, string preset)
    {
        if(!QuickClipBatch.Presets.Contains(preset))throw new ArgumentException("请选择有效的导出格式。");
        if(preset!="Fast Copy" && edits.Any(edit=>edit.OutputSegments.Count>64))throw new ArgumentException("单个视频合并最多 64 个片段，请减少片段数量或选择原格式 / 原码率。");
    }

    public static IReadOnlyList<Job> PrepareJoinedJobs(IEnumerable<ClipEditResult> edits, string preset, ConversionOptions exportOptions,
        string folder, bool outputToSource, string settingName, IEnumerable<string>? reserved = null)
    {
        var items=edits.ToArray();ValidateJoinedExports(items,preset);
        var used=new HashSet<string>(reserved??[],OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        var jobs=new List<Job>();
        foreach(var edit in items)
        {
            var inputs=PrepareExports([edit],preset,exportOptions);
            var options=preset=="Fast Copy"?inputs[0].Options.Clone():exportOptions.Clone();
            if(preset!="Fast Copy"){options.Format=preset.ToLowerInvariant();options.CopyStreams=false;}
            // One retained range can go straight through clipping, including its editing settings.
            var single=inputs.Count==1;
            if(single)options=inputs[0].Options.Clone();
            else options.Start=options.End=0;
            var target=outputToSource?Path.GetDirectoryName(Path.GetFullPath(edit.Path))!:folder;
            var grouped=ConversionBatch.CreateJobs(Catalog.Find(single?"clip":"join"),inputs.Select(input=>input.Path).ToArray(),target,options,
                single?null:inputs.Select(input=>input.Options).ToArray(),used);
            foreach(var job in grouped)
            {
                var name=Path.GetFileNameWithoutExtension(edit.Path);
                if(!string.IsNullOrWhiteSpace(settingName))name+=" ["+settingName+"]";
                else if(!edit.RemoveSelected)name+=" [People]";
                job.Output=MediaEngine.UniqueOutput(target,name,options.Format,used);used.Add(job.Output);jobs.Add(job);
            }
        }
        return jobs;
    }

    public static IReadOnlyList<QuickClipInput> PrepareExports(IEnumerable<ClipEditResult> edits,
        string preset, ConversionOptions exportOptions)
    {
        var result = new List<QuickClipInput>();
        foreach (var edit in edits)
        {
            var segments = edit.OutputSegments;
            if (!edit.Info.HasVideo || edit.Info.Duration <= 0 || segments.Count == 0)
                throw new ArgumentException("请选择有效视频并至少保留一个片段。");
            foreach (var segment in segments)
            {
                var options = segment.Clone();
                options.VideoCodec = exportOptions.VideoCodec;
                options.AudioCodec = exportOptions.AudioCodec;
                options.Quality = exportOptions.Quality;
                options.VideoRateMode = exportOptions.VideoRateMode;
                options.VideoBitrate = exportOptions.VideoBitrate;
                options.Width = exportOptions.Width;
                options.Height = exportOptions.Height;
                options.Fps = exportOptions.Fps;
                options.AudioBitrate = exportOptions.AudioBitrate;
                options.SampleRate = exportOptions.SampleRate;
                options.AudioChannels = exportOptions.AudioChannels;
                options.KeepAllAudioStreams = exportOptions.KeepAllAudioStreams;
                options.KeepMetadata = exportOptions.KeepMetadata;
                options.Threads = exportOptions.Threads;
                options = QuickClipBatch.ResolveOptions(edit.Path, preset, options);
                var job = new Job { FeatureId = "clip", Inputs = [edit.Path], Options = options,
                    Output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AvaMedia-clip-validation." + options.Format) };
                MediaEngine.Validate(job);
                MediaEngine.ValidateEdits(job, [edit.Info]);
                result.Add(new(edit.Path, options));
            }
        }
        if (result.Count == 0) throw new ArgumentException("请至少编辑一个视频。");
        return result;
    }
}
