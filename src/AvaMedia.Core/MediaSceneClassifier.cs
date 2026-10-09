namespace AvaMedia.Core;

public sealed record MediaSceneMatch(string Label, string Category, double Similarity, double Margin)
{
    public bool RequiresMargin { get; init; } = true;
    public bool Qualifies(double similarity, double margin) => Similarity >= similarity && (!RequiresMargin || Margin >= margin);
}
public sealed record MediaSceneFrame(double Seconds, IReadOnlyList<MediaSceneMatch> Matches)
{ public IReadOnlyList<MediaSceneMatch> Candidates { get; init; } = []; }
public sealed record MediaSceneScore(string Label, string Category, double Similarity, int MatchedFrames);
public sealed record MediaSceneResult(string Model, string Backend, string? FallbackReason,
    double MinimumSimilarity, double MinimumMargin, IReadOnlyList<MediaSceneScore> Scores, IReadOnlyList<MediaSceneFrame> Frames);

/// <summary>Built-in scenes and selected semantic candidates share the tagger's samples.</summary>
internal sealed class MediaSceneClassifier(GemmaMediaEmbedding embedding, WordCandidate[] candidates, float[][] labels) : IAsyncDisposable
{
    private const double MinimumSimilarity = .55;
    private const double MinimumMargin = .03;

    public static async Task<MediaSceneClassifier> CreateAsync(ModelStore store, MediaTagOptions options, AiActivityReporter activity, CancellationToken ct)
    {
        // Never download here: the UI asks before fetching the semantic model; workers skip scenes instead.
        if (!await store.IsInstalledAsync(ModelCatalog.EmbeddingId, ct: ct).ConfigureAwait(false))
            throw new SemanticModelMissingException();
        var embedding = await GemmaMediaEmbedding.StartAsync(store, ct, options.PreferGpu, stage => activity.Stage(stage)).ConfigureAwait(false);
        try
        {
            var defaults = options.RecognizeScenes
                ? WordLibraryCatalog.SceneEntries.Where(entry => entry.Category is "场景空间" or "照明状态" or "面部可见性") : [];
            var candidates = defaults.Concat(options.SemanticCandidates).DistinctBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            var labels = new List<float[]>();
            activity.Backend(embedding.Backend);
            activity.Stage("准备语义描述", 0, candidates.Length, "词");
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
        IReadOnlyList<int> samples, AiActivityReporter activity, CancellationToken ct, Action<MediaSceneResult>? updated = null)
    {
        activity.Stage("识别语义标签", 0, images.Count, "帧");
        activity.Backend(embedding.Backend);
        var matches = new List<MediaSceneMatch[]>();
        var observations = new List<MediaSceneMatch[]>();
        var groups = candidates.Select((entry, index) => (entry.Category, Index: index)).GroupBy(entry => entry.Category).ToArray();
        MediaSceneResult Snapshot()
        {
            var frames = seconds.Select((time, index) => (time, index)).Where(item => samples[item.index] < matches.Count)
                .Select(item => new MediaSceneFrame(item.time, matches[samples[item.index]]) { Candidates = observations[samples[item.index]] }).ToArray();
            var totals = frames.SelectMany(frame => frame.Matches).GroupBy(match => (match.Label, match.Category))
                .Select(group => new MediaSceneScore(group.Key.Label, group.Key.Category, group.Max(match => match.Similarity), group.Count()))
                .OrderByDescending(score => score.Similarity).ToArray();
            return new(ModelCatalog.EmbeddingId, embedding.Backend, embedding.FallbackReason, MinimumSimilarity, MinimumMargin, totals, frames);
        }
        for (var offset = 0; offset < images.Count; offset += 4)
        {
            var vectors = await embedding.EmbedImagesAsync(images.Skip(offset).Take(4).ToArray(), ct).ConfigureAwait(false);
            foreach (var vector in vectors)
            {
                var scores = labels.Select(label => GemmaMediaEmbedding.Cosine(vector, label)).ToArray();
                var selected = new List<MediaSceneMatch>();
                var raw = new List<MediaSceneMatch>();
                foreach (var group in groups)
                {
                    var ranked = group.Select(entry => entry.Index).OrderByDescending(index => scores[index]).ToArray();
                    // Scene alternatives compete; objects, features and custom descriptions can coexist.
                    var exclusive = ranked.Length > 1 && group.Key is "场景空间" or "照明状态" or "面部可见性" or "内容分级";
                    var best = ranked[0];
                    var observationsForGroup = ranked.Select(index => new MediaSceneMatch(candidates[index].Label, candidates[index].Category,
                        scores[index], exclusive ? scores[index] - scores[index == best ? ranked[1] : best] : 0)
                        { RequiresMargin = exclusive }).ToArray();
                    raw.AddRange(observationsForGroup);
                    selected.AddRange(observationsForGroup.Where(match => match.Qualifies(MinimumSimilarity, MinimumMargin)
                        && !WordLibraryCatalog.IsSemanticBaseline(match.Label)));
                }
                matches.Add(selected.ToArray());
                observations.Add(raw.ToArray());
            }
            activity.Backend(embedding.Backend);
            activity.Advance(matches.Count, images.Count, "帧");
            updated?.Invoke(Snapshot());
        }
        return Snapshot();
    }

    public ValueTask DisposeAsync() => embedding.DisposeAsync();
}

/// <summary>The optional semantic model is not installed; callers skip scene/semantic work rather than download implicitly.</summary>
public sealed class SemanticModelMissingException() : InvalidOperationException("语义模型未下载，已跳过场景识别。");
