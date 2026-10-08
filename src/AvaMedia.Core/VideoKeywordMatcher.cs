using System.Diagnostics;

namespace AvaMedia.Core;

public sealed record SemanticKeyword(string Label, string Description);
public sealed record VideoKeywordOptions(int Frames = 8, double MinimumSimilarity = .55, double MinimumMargin = .03, bool ReuseSimilarFrames = true)
{
    public void Validate()
    {
        if (Frames is < 1 or > 32 || !double.IsFinite(MinimumSimilarity) || MinimumSimilarity is < 0 or > 1
            || !double.IsFinite(MinimumMargin) || MinimumMargin is < 0 or > 1)
            throw new ArgumentException("语义匹配参数超出范围。");
    }
}
public sealed record VideoKeywordScore(string Keyword, double Similarity);
public sealed record VideoKeywordResult(string Path, IReadOnlyList<VideoKeywordScore> Scores, bool IsMatch,
    int SampledFrames, long Length, DateTime LastWriteUtc, int InferredFrames)
{
    public int ReusedFrames => SampledFrames - InferredFrames;
    public string Keyword => Scores[0].Keyword;
    public double Similarity => Scores[0].Similarity;
    public double Margin => Scores.Count > 1 ? Similarity - Scores[1].Similarity : 1;
}
public sealed record VideoKeywordProgress(int Frame, int TotalFrames);

/// <summary>Samples a bounded number of frames, reuses similar images and batches unique embeddings.</summary>
public sealed class VideoKeywordMatcher : IAsyncDisposable
{
    private readonly IMediaEngine _engine;
    private readonly GemmaVideoEmbedding _embedding;
    private readonly SemanticKeyword[] _keywords;
    private readonly float[][] _labels;
    private VideoKeywordMatcher(IMediaEngine engine, GemmaVideoEmbedding embedding, SemanticKeyword[] keywords, float[][] labels)
    { _engine = engine; _embedding = embedding; _keywords = keywords; _labels = labels; }

    public static SemanticKeyword[] ParseKeywords(string text)
    {
        var result = new List<SemanticKeyword>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('=');
            var label = (split < 0 ? entry : entry[..split]).Trim();
            var description = (split < 0 ? entry : entry[(split + 1)..]).Trim();
            BatchVideoTools.ValidateRenameKeyword(label);
            if (string.IsNullOrWhiteSpace(description) || description.Length > 512) throw new ArgumentException("关键词描述不能为空或超过 512 字符。");
            if (!labels.Add(label)) throw new ArgumentException("命名关键词重复：" + label);
            result.Add(new(label, description));
        }
        if (result.Count is < 1 or > WordLibraryCatalog.MaximumCandidates) throw new ArgumentException("请输入 1–20000 个关键词，使用逗号或换行分隔。");
        return result.ToArray();
    }

    public static Task<VideoKeywordMatcher> CreateAsync(IMediaEngine engine, IReadOnlyList<SemanticKeyword> keywords,
        ModelStore? store = null, CancellationToken ct = default, bool preferGpu = true) => Task.Run(async () =>
    {
        var snapshot = keywords.ToArray();
        if (snapshot.Length is < 1 or > WordLibraryCatalog.MaximumCandidates) throw new ArgumentException("请选择 1–20000 个关键词。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in snapshot)
        {
            BatchVideoTools.ValidateRenameKeyword(keyword.Label);
            if (!names.Add(keyword.Label) || string.IsNullOrWhiteSpace(keyword.Description) || keyword.Description.Length > 512)
                throw new ArgumentException("候选名称重复或描述无效：" + keyword.Label);
        }
        var embedding = await GemmaVideoEmbedding.StartAsync(store ?? new(), ct, preferGpu).ConfigureAwait(false);
        try
        {
            var labels = new List<float[]>();
            for (var offset = 0; offset < snapshot.Length;)
            {
                ct.ThrowIfCancellationRequested();
                // Keep long custom descriptions within the local runtime's context as well as its item limit.
                var batch = new List<string>(); var characters = 0;
                foreach (var keyword in snapshot.Skip(offset).Take(32))
                {
                    var cost = keyword.Description.Length + 64;
                    if (batch.Count > 0 && characters + cost > 2048) break;
                    batch.Add(keyword.Description); characters += cost;
                }
                labels.AddRange(await embedding.EmbedLabelsAsync(batch, ct).ConfigureAwait(false));
                offset += batch.Count;
            }
            return new VideoKeywordMatcher(engine, embedding, snapshot, labels.ToArray());
        }
        catch { await embedding.DisposeAsync(); throw; }
    }, ct);

    public Task<VideoKeywordResult> MatchAsync(string path, VideoKeywordOptions options,
        IProgress<VideoKeywordProgress>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        options.Validate();
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("源文件不存在，请刷新文件列表。", path);
        var length = file.Length; var modified = file.LastWriteTimeUtc;
        var info = await _engine.Probe(path, ct).ConfigureAwait(false);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("请选择有有效时长的视频。");
        var (duration, frameRate) = BatchVideoTools.VideoTiming(info);
        var frames = (int)Math.Min(options.Frames, Math.Max(1, Math.Ceiling(duration * 2)));
        var totals = new double[_keywords.Length];
        var unique = new List<(byte[] Png, byte[] Signature)>();
        var samples = new int[frames];
        for (var index = 0; index < frames; index++)
        {
            ct.ThrowIfCancellationRequested();
            var seconds = Math.Min(duration * (index + .5) / frames, Math.Max(0, duration - Math.Max(.08, 1 / frameRate)));
            var png = await ReadFrameAsync(path, info.VideoStreamIndex, seconds, ct).ConfigureAwait(false);
            var signature = options.ReuseSimilarFrames ? VideoFrameSimilarity.FromEncoded(png) : [];
            var existing = options.ReuseSimilarFrames ? unique.FindIndex(frame => VideoFrameSimilarity.Similar(frame.Signature, signature)) : -1;
            samples[index] = existing >= 0 ? existing : unique.Count;
            if (existing < 0) unique.Add((png, signature));
        }
        var completed = 0;
        for (var offset = 0; offset < unique.Count; offset += 4)
        {
            ct.ThrowIfCancellationRequested();
            var batch = unique.Skip(offset).Take(4).Select(frame => frame.Png).ToArray();
            var vectors = await _embedding.EmbedImagesAsync(batch, ct).ConfigureAwait(false);
            for (var index = 0; index < vectors.Length; index++)
            {
                // Weight reused frames exactly as the original uniformly spaced samples.
                var weight = samples.Count(sample => sample == offset + index);
                for (var label = 0; label < totals.Length; label++) totals[label] += weight * GemmaVideoEmbedding.Cosine(vectors[index], _labels[label]);
                completed += weight;
            }
            progress?.Report(new(completed, frames));
        }
        file.Refresh();
        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != modified)
            throw new IOException("分析期间源文件已改变，请重新匹配：" + path);
        var scores = _keywords.Select((keyword, index) => new VideoKeywordScore(keyword.Label, totals[index] / frames))
            .OrderByDescending(score => score.Similarity).ToArray();
        var margin = scores.Length > 1 ? scores[0].Similarity - scores[1].Similarity : 1;
        return new VideoKeywordResult(path, scores, scores[0].Similarity >= options.MinimumSimilarity && margin >= options.MinimumMargin,
            frames, length, modified, unique.Count);
    }, ct);

    private async Task<byte[]> ReadFrameAsync(string path, int stream, double seconds, CancellationToken ct)
    {
        using var process = await ProcessRunner.StartAsync(_engine.FFmpeg,
            ["-v", "error", "-nostdin", "-ss", MediaEngine.Number(seconds), "-i", path, "-map", $"0:v:{stream}", "-frames:v", "1",
                "-vf", "scale=512:512:force_original_aspect_ratio=decrease,setsar=1", "-c:v", "png", "-f", "image2pipe", "pipe:1"], ct).ConfigureAwait(false);
        using var cancellation = ct.Register(() => Kill(process));
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var data = new MemoryStream();
            // Bound corrupt/unexpected decoder output rather than buffering it indefinitely.
            var buffer = new byte[65536];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (data.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("视频画面数据超出范围。");
                data.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0 || data.Length == 0) throw new InvalidDataException("视频抽帧失败：" + await error.ConfigureAwait(false));
            return data.ToArray();
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await error.ConfigureAwait(false);
        }
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    public ValueTask DisposeAsync() => _embedding.DisposeAsync();
}
