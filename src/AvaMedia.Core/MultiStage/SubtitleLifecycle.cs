using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record SubtitleOutputChoice(string Mode = "file", string Format = "srt");

public static class SubtitleLifecycle
{
    public static IReadOnlyList<PageDescriptor> Pages { get; } = [new("process", "识别处理", PageKind.Processing),
        new("choice", "输出方式", PageKind.Interaction), new("generate", "生成", PageKind.Processing), new("done", "完成", PageKind.Result)];

    public static void PrepareRecognition(Job job)
    {
        job.Options.Transcription ??= new();
        job.Options.Transcription.ReviewOutputFolder ??= Path.GetDirectoryName(job.Output);
        job.Options.Transcription.RecognitionOnly = true;
        job.Options.Format = "srt"; job.Output = AiTaskResults.PathFor(job, "draft", "srt");
    }

    public static string OutputFormat(SubtitleOutputChoice choice) => choice.Mode switch
    { "file" when choice.Format is "srt" or "ass" => choice.Format, "soft" or "hard" => "mp4", _ => throw new ArgumentException("请选择有效的字幕输出方式。") };

    internal static async Task GenerateAsync(IMediaEngine engine, Job job, TaskLifecycle lifecycle, Action<double> progress, CancellationToken ct)
    {
        lifecycle.StartProcessing("generate");
        try
        {
            var choice = lifecycle.Pages.Single(p => p.Page.Id == "choice").Result!.Value.Deserialize<SubtitleOutputChoice>()!;
            var transcript = job.SubtitleResult ?? await AiTaskResults.LoadAsync<SubtitleTaskResult>(AiTaskResults.PathFor(job, "subtitles"), ct)
                ?? throw new IOException("字幕结果缺失，请重新识别。");
            var source = new FileInfo(job.Inputs[0]);
            if (transcript.IsPartial || !source.Exists || source.Length != transcript.SourceLength || source.LastWriteTimeUtc != transcript.SourceWriteUtc)
                throw new IOException("源文件已更改，请重新识别字幕。");
            var options = job.Options.Clone();
            options.Format = OutputFormat(choice);
            var draft = new Job { FeatureId = "auto-subtitle", Inputs = job.Inputs, Output = job.Output, Options = options };
            if (choice.Mode == "soft")
            {
                if (options.Speed != 1) throw new ArgumentException("软字幕暂不支持变速，请选择硬字幕。");
                var subtitle = AiTaskResults.PathFor(job, "export", "srt");
                await File.WriteAllTextAsync(subtitle, SpeechSubtitles.Srt(transcript.Cues), new UTF8Encoding(false), ct).ConfigureAwait(false);
                options.Transcription = null; options.SubtitleMode = SubtitleMode.ExternalTrack; options.Subtitle = subtitle;
                options.SubtitleStreamIndex = 0; options.CopyStreams = true;
                draft.FeatureId = "mp4";
            }
            else
            {
                options.Transcription ??= new();
                options.Transcription.RecognitionOnly = false;
                options.Transcription.ReviewedCues = transcript.Cues;
                options.Transcription.ReviewedSourceLength = transcript.SourceLength;
                options.Transcription.ReviewedSourceWriteUtc = transcript.SourceWriteUtc;
                // This child execution reuses the existing SRT/ASS and burn-in paths without another recognition pass.
            }
            draft.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Job.ProgressDetail)) job.ProgressDetail = draft.ProgressDetail; };
            try
            {
                if (choice.Mode == "soft") await engine.Execute(draft, progress, ct).ConfigureAwait(false);
                else
                {
                    SpeechSubtitleService.Validate(draft);
                    await new SpeechSubtitleService(engine).ExecuteAsync(draft, progress, ct, saveTaskResult: false).ConfigureAwait(false);
                }
            }
            finally { job.AppendLog(await draft.ReadLogAsync().ConfigureAwait(false)); }
            lifecycle.CompleteProcessing("generate", choice.Mode switch { "soft" => "软字幕 · MP4", "hard" => "硬字幕 · MP4", _ => options.Format.ToUpperInvariant() });
        }
        catch (Exception error) { lifecycle.Fail(error.Message); throw; }
    }
}
