using System.Text.Json;
using AvaMedia.Core;

internal static class LongSummaryProgressChecks
{
    public static void Run()
    {
        static void Check(bool value) { if (!value) throw new Exception("long summary progress contract"); }
        var id = Guid.NewGuid().ToString();
        var states = new[] { "pending", "running", "cached", "done", "degraded", "failed" };
        foreach (var state in states)
        {
            var input = new SummarySegmentProgress("s0000", 0, 240, state, state is "done" or "cached" or "degraded" ? "真实分段摘要" : "");
            SummaryProgressStore.Set(id, [input]); var snapshot = SummaryProgressStore.Get(id)!; Check(snapshot[0] == input);
            snapshot[0] = snapshot[0] with { Summary = "changed" }; Check(SummaryProgressStore.Get(id)![0] == input);
            var dto = new AiTaskProgressDto(id, "逐段理解", "", 40, "fake", 2, [], [], [], false, null, new(0, 0)) { SummarySegments = SummaryProgressStore.Get(id) };
            var decoded = JsonSerializer.Deserialize<AiTaskProgressDto>(JsonSerializer.Serialize(dto))!;
            Check(decoded.SummarySegments is [{ SegmentId: "s0000", Start: 0, End: 240 }] && decoded.SummarySegments[0].State == state);
        }
        var job = new Job { SummarySegments = [new(new("s0", 0, 240), new("摘要", [], [], [], [], [], "主题"))] };
        Check(!JsonSerializer.Serialize(job).Contains("真实分段摘要") && !JsonSerializer.Serialize(job).Contains("SummarySegments"));
        Console.WriteLine("PASS Phase 7 progress: six segment states, actual ranges/summaries, snapshot isolation, remote DTO, runtime-only job fields");
    }
}
