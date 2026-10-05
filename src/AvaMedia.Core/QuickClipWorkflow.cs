namespace AvaMedia.Core;

public sealed record ClipEditResult(string Path, MediaInfo Info, IReadOnlyList<ConversionOptions> Segments);

/// <summary>Export settings are applied without replacing any segment's editing draft.</summary>
public static class QuickClipWorkflow
{
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
