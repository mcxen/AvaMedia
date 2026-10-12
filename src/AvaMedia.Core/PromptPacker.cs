namespace AvaMedia.Core;

/// <summary>Packs the exact serialized request. The first owned cue remains present even if its text needs trimming.</summary>
public sealed class PromptPacker(ITokenCounter? counter = null)
{
    public ITokenCounter Counter { get; } = counter ?? new ConservativeTokenCounter();
    public int Estimate(SegmentJob job) => Counter.Count(LongSummaryJson.Write(job));
    public string Trim(string text, int tokens)
    {
        if (Counter.Count(text) <= tokens) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Counter.Count(text[..mid]) <= tokens) lo = mid; else hi = mid - 1;
        }
        if (lo > 0 && char.IsHighSurrogate(text[lo - 1])) lo--;
        return text[..lo];
    }
    public SegmentJob Pack(SegmentJob source, TokenBudget budget)
    {
        var context = new RollingContext(Trim(source.Context.PrevSummary, 120),
            source.Context.OpenThreads.Take(5).Select(t => Trim(t, 30)).ToArray(),
            source.Context.Entities.Take(10).Select(t => Trim(t, 10)).ToArray());
        while (Counter.Count(LongSummaryJson.Write(context)) > 400)
            context = context.Entities.Count > 0 ? context with { Entities = context.Entities.Take(context.Entities.Count - 1).ToArray() }
                : context with { OpenThreads = context.OpenThreads.Take(Math.Max(0, context.OpenThreads.Count - 1)).ToArray(), PrevSummary = "" };
        var job = source with { Context = context, Focus = Trim(source.Focus, 200),
            Frames = source.Frames.Take(budget.FrameLimit).Select(f => f with { Caption = Trim(f.Caption, 100), Ocr = Trim(f.Ocr, 50) }).ToArray() };
        var cues = job.Transcript.ToList(); var frames = job.Frames.ToList();
        var first = cues.FindIndex(c => !c.ContextOnly);
        var firstId = first < 0 ? null : cues[first].Id;
        while (Estimate(job with { Transcript = cues, Frames = frames }) > budget.Input)
        {
            if (frames.Count > 0) { frames.RemoveRange(frames.Count / 2, frames.Count - frames.Count / 2); continue; }
            var removable = cues.FindLastIndex(c => c.Id != firstId);
            if (removable >= 0)
            {
                var remove = Math.Max(1, cues.Count / 2);
                for (var i = cues.Count - 1; i >= 0 && remove > 0; i--)
                    if (cues[i].Id != firstId) { cues.RemoveAt(i); remove--; }
                continue;
            }
            if (cues.Count == 1 && cues[0].Text.Length > 0)
            {
                var overhead = Estimate(job with { Transcript = [cues[0] with { Text = "" }], Frames = [] });
                var text = Trim(cues[0].Text, Math.Max(0, budget.Input - overhead - 32));
                if (text.Length == cues[0].Text.Length) text = text[..(text.Length / 2)];
                cues[0] = cues[0] with { Text = text };
                continue;
            }
            job = job with { Context = RollingContext.Empty, Focus = "" };
            if (Estimate(job with { Transcript = cues, Frames = frames }) > budget.Input)
                throw new InvalidDataException("片段元数据超出模型预算。");
        }
        job = job with { Transcript = cues.ToArray(), Frames = frames.ToArray() };
        budget.Assert(LongSummaryJson.Write(job), Counter);
        return job;
    }
}
