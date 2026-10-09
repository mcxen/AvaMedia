namespace AvaMedia.Core;

/// <summary>Per-frame tag probabilities for encoded frames, in input order.</summary>
public interface IVideoFrameTagger
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, float>>> TagAsync(IReadOnlyList<byte[]> frames, CancellationToken ct);
}

public sealed record MinorSafetyHit(TimeSpan Timestamp, string Tag, double Score);
public sealed record MinorSafetyVerdict(bool Blocked, string? Reason, IReadOnlyList<MinorSafetyHit> Hits, int CheckedFrames);

/// <summary>
/// Hard safety rule agreed by the team: any sampled frame that JoyTag scores as possibly depicting a minor stops the
/// whole summary before a description or summary model sees anything. Not configurable on purpose: the tag list and
/// threshold are constants and there is no option, setting or parameter that disables the check.
/// </summary>
public static class MinorSafetyGuard
{
    /// <summary>Deliberately low (JoyTag's usual display threshold is 0.4): false positives abort, false negatives are the risk.</summary>
    public const double Threshold = 0.30;

    /// <summary>
    /// Danbooru/JoyTag tags that indicate a child or a childlike depiction. Entries absent from the shipped JoyTag
    /// vocabulary (loli, shota, toddler, ...) are kept so that a future vocabulary cannot silently drop them.
    /// </summary>
    public static readonly IReadOnlyList<string> Tags =
    [
        "loli", "shota", "oppai_loli", "child", "children", "kid", "toddler", "baby", "infant", "newborn",
        "female_child", "male_child", "child_on_child", "younger", "aged_down", "kindergarten_uniform", "randoseru",
        "elementary_school_student", "preschooler", "child_carry"
    ];
    private static readonly HashSet<string> TagSet = new(Tags, StringComparer.OrdinalIgnoreCase);

    public static MinorSafetyVerdict Evaluate(IReadOnlyList<TimeSpan> timestamps, IReadOnlyList<IReadOnlyDictionary<string, float>> scores)
    {
        // Fail closed: anything unexpected about the tagger output stops the job rather than letting frames through.
        if (timestamps.Count == 0) throw new InvalidDataException("安全检查没有可检查的画面，已停止视频总结。");
        if (scores.Count != timestamps.Count) throw new InvalidDataException("安全检查结果与采样画面数量不一致，已停止视频总结。");
        if (scores.Any(frame => frame is null || !frame.Keys.Any(TagSet.Contains)))
            throw new InvalidDataException("安全检查标签表缺少未成年人相关标签，已停止视频总结。请重新下载 JoyTag。");
        var hits = new List<MinorSafetyHit>();
        for (var index = 0; index < scores.Count; index++)
            foreach (var (tag, score) in scores[index])
            {
                if (!TagSet.Contains(tag)) continue;
                if (!float.IsFinite(score)) throw new InvalidDataException("安全检查输出无效，已停止视频总结。");
                if (score >= Threshold) hits.Add(new(timestamps[index], tag, score));
            }
        if (hits.Count == 0) return new(false, null, [], scores.Count);
        var frames = hits.Select(hit => hit.Timestamp).Distinct().Count();
        var top = hits.OrderByDescending(hit => hit.Score).Take(3)
            .Select(hit => $"{MediaTime.Format(hit.Timestamp.TotalSeconds)} {hit.Tag} {hit.Score:0.00}");
        return new(true, $"检测到可能涉及未成年人的画面（{frames} 帧，阈值 {Threshold:0.00}；{string.Join("，", top)}）。按安全规则不调用任何描述或总结模型，未生成画面描述。",
            hits, scores.Count);
    }

    public static async Task<MinorSafetyVerdict> CheckAsync(IReadOnlyList<VideoSummaryFrame> frames, IVideoFrameTagger tagger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tagger);
        if (frames.Count == 0) throw new InvalidDataException("安全检查没有可检查的画面，已停止视频总结。");
        var scores = await tagger.TagAsync(frames.Select(frame => frame.Image).ToArray(), ct).ConfigureAwait(false);
        return Evaluate(frames.Select(frame => frame.Timestamp).ToArray(), scores);
    }
}

/// <summary>JoyTag ONNX (same model and preprocessing as MediaTagService) scoring only the safety tags.</summary>
public sealed class JoyTagFrameTagger(ModelStore store, bool preferGpu = false) : IVideoFrameTagger
{
    private const int Batch = 4;

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, float>>> TagAsync(IReadOnlyList<byte[]> frames, CancellationToken ct)
    {
        using var lease = await store.AcquireAsync(ModelCatalog.JoyTagId, ct).ConfigureAwait(false);
        var tags = (await File.ReadAllLinesAsync(Path.Combine(lease.Directory, ModelCatalog.JoyTagLabels), ct).ConfigureAwait(false))
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).ToArray();
        if (tags.Length != 5813) throw new InvalidDataException("模型标签文件无效，请重新下载 JoyTag。");
        var watched = MinorSafetyGuard.Tags.Select(tag => Array.IndexOf(tags, tag)).Where(index => index >= 0).ToArray();
        if (watched.Length == 0) throw new InvalidDataException("JoyTag 标签表缺少安全检查标签，请重新下载 JoyTag。");
        return await Task.Run(() =>
        {
            using var session = new ModelInferenceSession(Path.Combine(lease.Directory, ModelCatalog.JoyTagFile),
                ModelCatalog.Find(ModelCatalog.JoyTagId).Files[0].Sha256, preferGpu, Batch);
            var results = new List<IReadOnlyDictionary<string, float>>(frames.Count);
            for (var offset = 0; offset < frames.Count; offset += Batch)
            {
                var vectors = MediaTagService.Predict(session, frames.Skip(offset).Take(Batch).ToArray(), Batch, tags.Length, ct);
                foreach (var vector in vectors)
                    results.Add(watched.ToDictionary(index => tags[index], index => vector[index], StringComparer.OrdinalIgnoreCase));
            }
            return (IReadOnlyList<IReadOnlyDictionary<string, float>>)results;
        }, ct).ConfigureAwait(false);
    }
}
