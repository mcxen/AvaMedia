namespace AvaMedia.Core;

public sealed record ClipEditResult(string Path, MediaInfo Info, IReadOnlyList<ConversionOptions> Segments);

/// <summary>Export settings are applied without replacing any segment's editing draft.</summary>
public static class QuickClipWorkflow
{
    public static void ValidateJoinedExports(IEnumerable<ClipEditResult> edits, string preset)
    {
        if(!QuickClipBatch.Presets.Contains(preset))throw new ArgumentException("请选择有效的导出格式。");
        if(preset!="Fast Copy" && edits.Any(edit=>edit.Segments.Count>64))throw new ArgumentException("单个视频合并最多 64 个片段，请选择分别导出。");
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
                var name=Path.GetFileNameWithoutExtension(edit.Path)+" ["+(string.IsNullOrWhiteSpace(settingName)?"People":settingName)+"]";
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
            if (!edit.Info.HasVideo || edit.Info.Duration <= 0 || edit.Segments.Count == 0)
                throw new ArgumentException("请选择有效视频并至少保留一个片段。");
            foreach (var segment in edit.Segments)
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
