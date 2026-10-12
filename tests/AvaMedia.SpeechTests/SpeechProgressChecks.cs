using AvaMedia.Core;

internal static class SpeechProgressChecks
{
    public static void Run()
    {
        static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
        }
        var id = Guid.NewGuid().ToString();
        var progress = new AiProgressSpeechTracker(id, 12.5, 601.25);
        SpeechProgressSnapshot Read() => AiProgressStore.GetSpeech(id)!;
        var initial = Read();
        Check(initial.CurrentSegment == -1 && initial.Segments.Length == 3
            && initial.Segments[0].StartSeconds == 12.5 && initial.Segments[2].EndSeconds == 613.75
            && initial.Segments.All(segment => segment.State == "pending"), "trimmed source has two full chunks and a short tail");
        progress.Begin(0);
        progress.Detected([new(10, 14), new(311, 314)]);
        progress.Advance(100); progress.Advance(90); progress.Cue();
        Check(Read().Segments[0].RecognizedSeconds == 87.5 && Read().CueCount == 1
            && Read().Segments[0].CueCount == 1, "recognized duration is monotonic and cues belong to the active chunk");
        progress.Advance(999); progress.Complete();
        progress.Begin(1);
        progress.Detected([new(311, 314)]);
        progress.Complete();
        progress.Begin(2); progress.Detected([]); progress.Complete();
        var complete = Read();
        Check(complete.Segments.Select(segment => segment.State).SequenceEqual(["done", "done", "empty"])
            && complete.Segments.Sum(segment => segment.RecognizedSeconds) == 601.25,
            "completed and silent chunks account for exactly the selected duration");
        Check(complete.VoiceIntervals.SequenceEqual([new VoiceInterval(12.5, 14), new VoiceInterval(311, 314)])
            && complete.Segments[0].VoiceIntervalCount == 2 && complete.Segments[1].VoiceIntervalCount == 1,
            "overlapping VAD windows are clipped to source bounds and merged across chunks");
        complete.Segments[0] = complete.Segments[0] with { State = "pending" };
        Check(Read().Segments[0].State == "done" && initial.Segments[0].State == "pending",
            "stored and previously read snapshots remain isolated from later changes");
        var exactId = Guid.NewGuid().ToString();
        _ = new AiProgressSpeechTracker(exactId, 0, 600);
        Check(AiProgressStore.GetSpeech(exactId)!.Segments.Length == 2, "exact chunk boundaries do not create an empty tail");
    }
}
