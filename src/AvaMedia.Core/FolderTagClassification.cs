namespace AvaMedia.Core;

/// <summary>Classify supported labels directly, retaining uncertainty and missing full frame vectors.</summary>
internal static class FolderTagClassification
{
    internal static FolderClassificationDecision Decide(MediaTagResult media, FolderClassificationRule rule)
    {
        var threshold = rule.UseAutomaticSettings ? .4 : rule.Threshold;
        var margin = rule.UseAutomaticSettings ? .04 : rule.Margin;
        var categories = rule.Categories.Where(category => category.Tags.Length > 0).ToArray();
        var indices = media.Scores.Select((score, index) => (score.Tag, index)).ToDictionary(item => item.Tag, item => item.index, StringComparer.OrdinalIgnoreCase);
        var frames = new List<FolderFrameClassification>();
        var observations = categories.ToDictionary(category => category.Id, _ => new List<double>());
        var samples = VideoFormats.IsVideo(media.Path) ? media.Frames.Select(frame => (frame.Seconds, Values: frame.Values.Select(value => (double)value).ToArray()))
            : [(Seconds: 0d, Values: media.Scores.Select(score => score.Score).ToArray())];
        foreach (var sample in samples)
        {
            double? Value(FolderClassificationCategory category)
            {
                var alternatives = category.Tags.Select(tags => tags.Select(tag => indices.TryGetValue(tag, out var index) && index < sample.Values.Length
                    && double.IsFinite(sample.Values[index]) && sample.Values[index] is >= 0 and <= 1 ? (double?)sample.Values[index] : null).ToArray()).ToArray();
                return alternatives.Any(values => values.Any(value => value is null)) ? null : alternatives.Max(values => values.Min()!.Value);
            }
            if (VideoFormats.IsVideo(media.Path) && sample.Values.Length != media.Scores.Count)
            { frames.Add(new(sample.Seconds, null, null, null)); continue; }
            var values = categories.ToDictionary(category => category.Id, Value);
            var ranked = categories.Select(category => (Category: category, Score: values[category.Id] is null ? null
                : category.SupersededBy.Any(id => values.GetValueOrDefault(id) >= threshold) ? 0d : values[category.Id])).OrderByDescending(item => item.Score).ToArray();
            foreach (var category in categories.Where(category => values[category.Id].HasValue)) observations[category.Id].Add(values[category.Id]!.Value);
            if (ranked.Any(item => item.Score is null)) { frames.Add(new(sample.Seconds, null, null, null)); continue; }
            var top = ranked[0]; var gap = top.Score!.Value - (ranked.Length > 1 ? ranked[1].Score!.Value : 0);
            var match = top.Score >= threshold && gap >= margin && gap > 0 ? top.Category.Id
                : rule.FallbackCategoryId is not null && top.Score < Math.Max(0, threshold - .2) ? rule.FallbackCategoryId : null;
            frames.Add(new(sample.Seconds, match, top.Score, gap));
        }
        var scores = categories.Where(category => observations[category.Id].Count > 0).Select(category => new FolderCategoryScore(category.Id, category.Name,
            observations[category.Id].Max(), frames.Count(frame => frame.CategoryId == category.Id))).OrderByDescending(score => score.MatchedFrames).ThenByDescending(score => score.Similarity).ToArray();
        var matches = frames.Where(frame => frame.CategoryId is not null && (!rule.UsePeakEvidence || frame.CategoryId != rule.FallbackCategoryId))
            .GroupBy(frame => frame.CategoryId!).OrderByDescending(group => group.Count()).ToArray();
        var winner = matches.FirstOrDefault();
        var agreed = winner is null ? 0 : (double)winner.Count() / frames.Count;
        var required = rule.UseAutomaticSettings ? .6 : rule.MinimumAgreement;
        var complete = frames.Count > 0 && frames.All(frame => frame.Similarity.HasValue) && (!VideoFormats.IsVideo(media.Path) || frames.Count == media.SampledFrames);
        var accepted = complete && winner is not null && (matches.Length == 1 || winner.Count() > matches[1].Count()) && frames.All(frame => frame.Similarity.HasValue)
            && (rule.UsePeakEvidence || agreed >= required);
        var id = accepted ? winner!.Key : rule.UsePeakEvidence && complete && frames.All(frame => frame.CategoryId == rule.FallbackCategoryId) ? rule.FallbackCategoryId : null;
        var best = frames.Where(frame => frame.CategoryId == id).MaxBy(frame => frame.Similarity) ?? frames.MaxBy(frame => frame.Similarity);
        return new(rule.Id, rule.Name, id, rule.Categories.FirstOrDefault(category => category.Id == id)?.Name ?? "待确认", scores, frames, best?.Seconds, id is null ? agreed : (double)frames.Count(frame => frame.CategoryId == id) / frames.Count,
            id is null ? "标签不确定或采样画面有分歧" : id == rule.FallbackCategoryId ? "采样画面未检出所选标签" : "模型标签匹配");
    }
}
