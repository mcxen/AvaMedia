using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public sealed record VideoSummaryTranscriptSelection(IReadOnlyList<SubtitleCue> Cues, string[] Notes,
    string EvidenceKind, string GroundingGuidance);

/// <summary>Selects summary evidence without changing the original transcript or its exports.</summary>
public static class VideoSummaryTranscriptPolicy
{
    public const string SpeechGuidance = "speech_transcript 是 Whisper 自动识别、尚未核实的语句，不是已确认的作者旁白。只能支持识别到的说话内容，不能直接证明画面行为、身份、因果或作者意图。保留否定、条件和建议。画面明确而语句零散或与画面无关时，以场景、主体和主要活动为主，语音仅作辅助；不把背景语句写成主要叙事，不猜测声音种类。没有有效对白时依据画面总结，不编故事、不罗列无关物体。";
    public const string SubtitleGuidance = "transcript 是提供的字幕，保留其视频字幕或外部字幕来源；字幕内容不直接证明可见行为、身份或因果。保留否定、条件和建议，不把角色台词或背景语句改成作者观点。画面明确而字幕零散或无关时，以可见场景、主体和主要活动为主。";
    private static readonly Regex NoiseMarker = new(@"^\s*[\[（(【](?:音乐|背景音乐|噪声|静音|无语音|掌声|music|noise|silence|applause|no speech)[\]）)】]\s*[.!。！]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedPhrase = new(@"^(.{2,40}?)(?:[\s，,。.!！、]*\1){5,}[\s，,。.!！、]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static VideoSummaryTranscriptSelection Evaluate(IReadOnlyList<SubtitleCue> cues, string source, double duration,
        IReadOnlyList<VoiceInterval>? voiceIntervals = null)
    {
        var whisper = string.Equals(source, "Whisper", StringComparison.OrdinalIgnoreCase);
        var voices = voiceIntervals?.Where(v => double.IsFinite(v.StartSeconds) && double.IsFinite(v.EndSeconds) && v.EndSeconds > v.StartSeconds).ToArray() ?? [];
        var kept = new List<SubtitleCue>(); var noise = 0; var repetition = 0; var unsupported = 0;
        foreach (var cue in cues)
        {
            if (string.IsNullOrWhiteSpace(cue.Text)) continue;
            if (whisper && NoiseMarker.IsMatch(cue.Text)) { noise++; continue; }
            if (whisper && RepeatedPhrase.IsMatch(cue.Text.Trim())) { repetition++; continue; }
            // An empty VAD result may mean detection was unavailable; it never establishes silence.
            // Allow boundary tolerance and reject only cues wholly outside supplied speech intervals.
            var start = cue.Start.TotalSeconds; var end = cue.End.TotalSeconds;
            if (whisper && voices.Length > 0 && end > start && start >= 0 && end <= duration &&
                !voices.Any(v => v.StartSeconds - .5 < end && v.EndSeconds + .5 > start)) { unsupported++; continue; }
            kept.Add(cue);
        }
        var notes = new List<string>();
        if (whisper) notes.Add("Whisper 转写尚未核实；语言检测置信度不代表转写文本可信度。");
        if (noise + repetition + unsupported > 0)
            notes.Add($"总结证据排除明确非语句标记 {noise} 条、病理重复 {repetition} 条、无已有 VAD 区间支持的转写 {unsupported} 条；完整原文仍保留在转写与导出中。");
        if (kept.Count == 0) notes.Add("没有可用于总结的对白证据，依据可用画面概括场景和主要过程。");
        return new(kept, notes.ToArray(), whisper ? "speech_transcript" : "transcript",
            (whisper ? SpeechGuidance : SubtitleGuidance) + $" 来源：{source}。");
    }
}
