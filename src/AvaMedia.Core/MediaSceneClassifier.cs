namespace AvaMedia.Core;

public sealed record MediaSceneMatch(string Label, string Category, double Similarity, double Margin);
public sealed record MediaSceneFrame(double Seconds, IReadOnlyList<MediaSceneMatch> Matches);
public sealed record MediaSceneScore(string Label, string Category, double Similarity, int MatchedFrames);
public sealed record MediaSceneResult(string Model, string Backend, string? FallbackReason,
    double MinimumSimilarity, double MinimumMargin, IReadOnlyList<MediaSceneScore> Scores, IReadOnlyList<MediaSceneFrame> Frames);

/// <summary>Local scene candidates share the tagger's samples; cosine scores are separate from JoyTag outputs.</summary>
internal sealed class MediaSceneClassifier(GemmaMediaEmbedding embedding, WordCandidate[] candidates, float[][] labels) : IAsyncDisposable
{
    private const double MinimumSimilarity = .55;
    private const double MinimumMargin = .03;

    public static async Task<MediaSceneClassifier> CreateAsync(ModelStore store, bool preferGpu, AiActivityReporter activity, CancellationToken ct)
    {
        if (!await store.IsInstalledAsync(ModelCatalog.EmbeddingId, ct: ct).ConfigureAwait(false))
        {
            activity.Stage("下载场景模型");
            await store.DownloadAsync(ModelCatalog.EmbeddingId, new DownloadProgress(activity), ct).ConfigureAwait(false);
        }
        var embedding = await GemmaMediaEmbedding.StartAsync(store, ct, preferGpu, stage => activity.Stage(stage)).ConfigureAwait(false);
        try
        {
            var candidates = WordLibraryCatalog.SceneEntries.Where(entry => entry.Category is "场景空间" or "照明状态").ToArray();
            var labels = new List<float[]>();
            activity.Backend(embedding.Backend);
            activity.Stage("准备场景描述", 0, candidates.Length, "词");
            for (var offset = 0; offset < candidates.Length; offset += 16)
            {
                labels.AddRange(await embedding.EmbedLabelsAsync(candidates.Skip(offset).Take(16).Select(entry => entry.Description).ToArray(), ct).ConfigureAwait(false));
                activity.Advance(labels.Count, candidates.Length, "词");
            }
            return new(embedding, candidates, labels.ToArray());
        }
        catch { await embedding.DisposeAsync(); throw; }
    }

    public async Task<MediaSceneResult> AnalyzeAsync(IReadOnlyList<byte[]> images, IReadOnlyList<double> seconds,
        IReadOnlyList<int> samples, AiActivityReporter activity, CancellationToken ct)
    {
        activity.Stage("识别场景与照明", 0, images.Count, "帧");
        activity.Backend(embedding.Backend);
        var matches = new List<MediaSceneMatch[]>();
        var groups = candidates.Select((entry, index) => (entry.Category, Index: index)).GroupBy(entry => entry.Category).ToArray();
        for (var offset = 0; offset < images.Count; offset += 4)
        {
            var vectors = await embedding.EmbedImagesAsync(images.Skip(offset).Take(4).ToArray(), ct).ConfigureAwait(false);
            foreach (var vector in vectors)
            {
                var scores = labels.Select(label => GemmaMediaEmbedding.Cosine(vector, label)).ToArray();
                var selected = new List<MediaSceneMatch>();
                foreach (var group in groups)
                {
                    var ranked = group.Select(entry => entry.Index).OrderByDescending(index => scores[index]).ToArray();
                    var best = ranked[0]; var margin = scores[best] - scores[ranked[1]];
                    // Baselines compete with named scenes, but are not presented as positive findings.
                    if (scores[best] < MinimumSimilarity || margin < MinimumMargin
                        || candidates[best].Label is "其他室内" or "照明不明") continue;
                    selected.Add(new(candidates[best].Label, candidates[best].Category, scores[best], margin));
                }
                matches.Add(selected.ToArray());
            }
            activity.Backend(embedding.Backend);
            activity.Advance(matches.Count, images.Count, "帧");
        }
        var frames = seconds.Select((time, index) => new MediaSceneFrame(time, matches[samples[index]])).ToArray();
        var totals = frames.SelectMany(frame => frame.Matches).GroupBy(match => (match.Label, match.Category))
            .Select(group => new MediaSceneScore(group.Key.Label, group.Key.Category, group.Max(match => match.Similarity), group.Count()))
            .OrderByDescending(score => score.Similarity).ToArray();
        return new(ModelCatalog.EmbeddingId, embedding.Backend, embedding.FallbackReason,
            MinimumSimilarity, MinimumMargin, totals, frames);
    }

    public ValueTask DisposeAsync() => embedding.DisposeAsync();
    private sealed class DownloadProgress(AiActivityReporter activity) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => activity.Stage("下载场景模型", value.Received, value.Total, "字节", value.Stage);
    }
}
