using System.Text.Json;

namespace AvaMedia.Core;

public static class SegmentResultValidator
{
    public static JsonElement Schema { get; } = JsonDocument.Parse("""
    {"type":"object","additionalProperties":false,"required":["summary","events","claims","entities","openQuestions","threadsOut","topicLabel"],"properties":{
      "summary":{"type":"string","maxLength":600},"topicLabel":{"type":"string","maxLength":12},
      "events":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"required":["start","end","text","evidence"],"properties":{"start":{"type":"number"},"end":{"type":"number"},"text":{"type":"string"},"evidence":{"type":"array","items":{"type":"string"}}}}},
      "claims":{"type":"array","maxItems":10,"items":{"type":"object","additionalProperties":false,"required":["id","text","evidence","confidence"],"properties":{"id":{"type":"string"},"text":{"type":"string"},"evidence":{"type":"array","items":{"type":"string"}},"confidence":{"type":"string","enum":["high","medium","low"]}}}},
      "entities":{"type":"array","maxItems":15,"items":{"type":"object","additionalProperties":false,"required":["name","type","aliases"],"properties":{"name":{"type":"string"},"type":{"type":"string","enum":["person","org","product","place","term"]},"aliases":{"type":"array","items":{"type":"string"}}}}},
      "openQuestions":{"type":"array","maxItems":5,"items":{"type":"string"}},"threadsOut":{"type":"array","maxItems":5,"items":{"type":"string"}}
    }}
    """).RootElement.Clone();
    public static string[] Validate(SegmentJob job, SegmentResult? result)
    {
        if (result is null) return ["missing result"];
        var errors = new List<string>();
        if (result.Summary is null || result.Summary.Length > 600 || result.TopicLabel is null || result.TopicLabel.Length > 12) errors.Add("summary/topicLabel length");
        if (result.Events is null || result.Claims is null || result.Entities is null || result.OpenQuestions is null || result.ThreadsOut is null) return ["missing arrays"];
        if (result.Events.Length > 12 || result.Claims.Length > 10 || result.Entities.Length > 15 || result.OpenQuestions.Length > 5 || result.ThreadsOut.Length > 5) errors.Add("array limits");
        var ids = job.Transcript.Where(c => !c.ContextOnly).Select(c => c.Id).Concat(job.Frames.Select(f => f.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var ev in result.Events)
        {
            if (ev is null || !double.IsFinite(ev.Start) || !double.IsFinite(ev.End) || ev.Start < job.Start || ev.Start >= job.End || ev.End < ev.Start || ev.End > job.End) errors.Add("event time/ownership");
            if (ev is null || string.IsNullOrWhiteSpace(ev.Text) || ev.Text.Length > 600 || ev.Evidence is null || ev.Evidence.Length is < 1 or > 8 || ev.Evidence.Any(id => !ids.Contains(id))) errors.Add("event evidence");
        }
        foreach (var claim in result.Claims)
            if (claim is null || string.IsNullOrWhiteSpace(claim.Text) || claim.Text.Length > 600 || claim.Evidence is null || claim.Evidence.Length is < 1 or > 8 || claim.Evidence.Any(id => !ids.Contains(id)) || claim.Confidence is not ("high" or "medium" or "low")) errors.Add("claim evidence/confidence");
        if (result.Claims.Where(c => c is not null).Select(c => c.Id).Distinct().Count() != result.Claims.Length) errors.Add("duplicate claim ids");
        if (result.Entities.Any(e => e is null || string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 100 || e.Type is not ("person" or "org" or "product" or "place" or "term") || e.Aliases is null || e.Aliases.Length > 10)) errors.Add("entity contract");
        if (result.OpenQuestions.Concat(result.ThreadsOut).Any(t => t is null || t.Length > 200)) errors.Add("thread limits");
        return errors.Distinct().ToArray();
    }
}
