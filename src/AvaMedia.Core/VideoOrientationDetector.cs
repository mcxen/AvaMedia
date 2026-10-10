using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace AvaMedia.Core;

public enum OrientationReliability { Unknown, Medium, High }

/// <summary>Rotation is a clockwise correction of the currently displayed picture, not the encoded frame.</summary>
public sealed record VideoOrientationResult(int? Rotation, OrientationReliability Reliability,
    int SampledFrames, int ValidFrames, int AgreeingFrames, string Reason)
{
    public bool IsCertain => Rotation is not null;
    public string Description => Rotation is { } angle ? BatchRotate.Direction(angle) : "无法确定";
    public IReadOnlyList<OrientationFrameEvidence> Evidence { get; init; } = [];
}

public sealed record OrientationDetectionProgress(int CompletedFrames, int TotalFrames, double Seconds)
{ public VideoOrientationResult? PreviewResult { get; init; } }

public interface IVideoOrientationDetector
{
    Task<VideoOrientationResult> DetectAsync(string path, MediaInfo info,
        IProgress<OrientationDetectionProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Scores are evidence strengths, not calibrated probabilities.</summary>
public sealed record OrientationFrameEvidence(double Seconds, double Score0, double Score90,
    double Score180, double Score270)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public double[] Scores => [Score0, Score90, Score180, Score270];
}

public static class VideoOrientationPolicy
{
    public static VideoOrientationResult Decide(IReadOnlyList<OrientationFrameEvidence> frames)
    {
        if (frames.Count == 0) return new(null, OrientationReliability.Unknown, 0, 0, 0, "未取得可分析的画面。");
        var votes = new int[4]; var valid = 0;
        foreach (var frame in frames)
        {
            var scores = frame.Scores;
            if (scores.Any(s => !double.IsFinite(s) || s < 0 || s > 1))
                throw new ArgumentException("方向分数必须在 0 到 1 之间。");
            var ranked = Enumerable.Range(0, 4).OrderByDescending(i => scores[i]).ToArray();
            var best = scores[ranked[0]];
            if (best < .5) continue;
            valid++;
            // A face detected at several rotations is not itself evidence of an upright picture.
            if ((best - scores[ranked[1]]) / best >= .18) votes[ranked[0]]++;
        }
        var winner = Enumerable.Range(0, 4).OrderByDescending(i => votes[i]).First();
        var agree = votes[winner];
        var opposition = votes.Sum() - agree;
        string? reason = valid == 0 ? "没有检测到足够清晰且朝向可判断的人脸。"
            : valid < 3 ? "有效人脸画面不足 3 帧，请手动检查方向。"
            : opposition >= 2 ? "不同画面的方向不一致，可能存在镜头转向或人物姿态变化。"
            : agree < 3 || agree < valid * .8 ? "方向证据接近或不够一致，请手动检查。" : null;
        if (reason is not null) return new(null, OrientationReliability.Unknown, frames.Count, valid, agree, reason) { Evidence = frames.ToArray() };
        var reliability = valid >= 4 && agree >= valid * .9 ? OrientationReliability.High : OrientationReliability.Medium;
        return new(winner * 90, reliability, frames.Count, valid, agree, $"有效 {valid}/{frames.Count} 帧，{agree} 帧方向一致。") { Evidence = frames.ToArray() };
    }
}

/// <summary>Offline YuNet inference on sparse, autorotated FFmpeg frames. Does not identify people.</summary>
public sealed class VideoOrientationDetector(IMediaEngine engine) : IVideoOrientationDetector
{
    private const string ModelHash = "EBAFCE4E3C118D6554634BE5C27AB333B4C047A9A8C3FAF1D7CF93101C22F0F0";
    private static readonly Lazy<byte[]> Model = new(() =>
    {
        using var source = typeof(VideoOrientationDetector).Assembly.GetManifestResourceStream("AvaMedia.Core.YuNet.onnx")
            ?? throw new InvalidDataException("缺少人脸检测模型，请重新安装完整程序。");
        using var bytes = new MemoryStream(); source.CopyTo(bytes); var data = bytes.ToArray();
        if (Convert.ToHexString(SHA256.HashData(data)) != ModelHash) throw new InvalidDataException("人脸检测模型校验失败。");
        return data;
    });
    private static readonly string[] OutputNames =
        ["cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32"];

    public Task<VideoOrientationResult> DetectAsync(string path, MediaInfo info,
        IProgress<OrientationDetectionProgress>? progress = null, CancellationToken ct = default)
    {
        _ = BatchRotate.OutputSize(info, 0);
        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            using var session = new InferenceSession(Model.Value, options);
            var evidence = new List<OrientationFrameEvidence>();
            var count = info.FrameRate > 0 ? (int)Math.Clamp(Math.Floor(info.Duration * info.FrameRate), 1, 8) : 8;
            var times = Enumerable.Range(0, count).Select(i => info.Duration * (.05 + .9 * i / Math.Max(1, count - 1)))
                .Select(t => Math.Min(t, Math.Max(0, info.Duration - .001))).Distinct().ToArray();
            var completed = 0;
            foreach (var size in new[] { 320, 640 })
            {
                for (var i = 0; i < times.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var pixels = await ReadFrame(path, info.VideoStreamIndex, times[i], size, ct);
                    var scores = new double[4];
                    for (var direction = 0; direction < 4; direction++)
                    {
                        ct.ThrowIfCancellationRequested();
                        scores[direction] = ScoreDirection(session, pixels, size, direction * 90, ct);
                    }
                    if (size == 640)
                    {
                        var first = evidence[i].Scores;
                        for (var j = 0; j < 4; j++) scores[j] = Math.Max(first[j], scores[j]);
                        evidence[i] = new(times[i], scores[0], scores[1], scores[2], scores[3]);
                    }
                    else evidence.Add(new(times[i], scores[0], scores[1], scores[2], scores[3]));
                    progress?.Report(new(++completed, times.Length * 2, times[i]) { PreviewResult = VideoOrientationPolicy.Decide(evidence) });
                }
                var result = VideoOrientationPolicy.Decide(evidence);
                // Higher resolution can resolve weak detections or nearly tied candidates;
                // strong temporal contradictions should remain uncertain.
                if (result.IsCertain || result.AgreeingFrames >= 3 || size == 640)
                {
                    progress?.Report(new(times.Length * 2, times.Length * 2, times[^1]));
                    return result;
                }
            }
            throw new InvalidOperationException("未完成方向检测。");
        }, ct);
    }

    private async Task<byte[]> ReadFrame(string path, int stream, double seconds, int size, CancellationToken ct)
    {
        // FFmpeg applies the file display matrix before these filters, just as Thumbnail does.
        string[] args = ["-v", "error", "-ss", MediaEngine.Number(seconds), "-i", path,
            "-map", $"0:v:{stream}", "-frames:v", "1", "-vf",
            $"scale={size}:{size}:force_original_aspect_ratio=decrease,pad={size}:{size}:(ow-iw)/2:(oh-ih)/2,setsar=1",
            "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"];
        using var process = ProcessRunner.Start(engine.FFmpeg, args);
        using var registration = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream(size * size * 3);
        await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        await process.WaitForExitAsync(ct); ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidDataException(await error);
        var pixels = output.ToArray();
        if (pixels.Length != size * size * 3) throw new InvalidDataException("视频帧解码失败，请检查视频是否完整。");
        return pixels;
    }

    private static double ScoreDirection(InferenceSession session, byte[] bgr, int size, int angle, CancellationToken ct)
    {
        var plane = size * size; var tensor = new float[plane * 3];
        for (var y = 0; y < size; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
            {
                var (sx, sy) = angle switch
                {
                    90 => (y, size - 1 - x), 180 => (size - 1 - x, size - 1 - y),
                    270 => (size - 1 - y, x), _ => (x, y)
                };
                var source = (sy * size + sx) * 3; var target = y * size + x;
                for (var channel = 0; channel < 3; channel++) tensor[channel * plane + target] = bgr[source + channel];
            }
        }
        using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, size, size]);
        using var run = new RunOptions();
        using var cancel = ct.Register(() => run.Terminate = true);
        try
        {
            using var output = session.Run(run, new Dictionary<string, OrtValue> { [session.InputNames[0]] = input }, OutputNames);
            ct.ThrowIfCancellationRequested();
            return Decode(output, size);
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private sealed record Face(float X, float Y, float Width, float Height, float Confidence, float[] Points);

    private static double Decode(IDisposableReadOnlyCollection<OrtValue> output, int size)
    {
        var values = output.ToArray(); var faces = new List<Face>();
        for (var level = 0; level < 3; level++)
        {
            var stride = 8 << level; var columns = size / stride;
            var cls = values[level].GetTensorDataAsSpan<float>();
            var obj = values[level + 3].GetTensorDataAsSpan<float>();
            var boxes = values[level + 6].GetTensorDataAsSpan<float>();
            var points = values[level + 9].GetTensorDataAsSpan<float>();
            for (var index = 0; index < cls.Length; index++)
            {
                var confidence = MathF.Sqrt(Math.Clamp(cls[index], 0, 1) * Math.Clamp(obj[index], 0, 1));
                // Favor abstention over rotating a video based on a weak face-like background pattern.
                if (confidence < .85f) continue;
                var row = index / columns; var column = index % columns;
                var width = MathF.Exp(boxes[index * 4 + 2]) * stride;
                var height = MathF.Exp(boxes[index * 4 + 3]) * stride;
                if (!float.IsFinite(width) || !float.IsFinite(height) || width < 24 || height < 24) continue;
                var x = (column + boxes[index * 4]) * stride - width / 2;
                var y = (row + boxes[index * 4 + 1]) * stride - height / 2;
                var landmarks = new float[10];
                for (var p = 0; p < 5; p++)
                {
                    landmarks[p * 2] = (column + points[index * 10 + p * 2]) * stride;
                    landmarks[p * 2 + 1] = (row + points[index * 10 + p * 2 + 1]) * stride;
                }
                faces.Add(new(x, y, width, height, confidence, landmarks));
            }
        }
        var kept = new List<Face>(); var best = 0d;
        foreach (var face in faces.OrderByDescending(f => f.Confidence).Take(500))
        {
            if (kept.Any(other => Overlap(face, other) > .3)) continue;
            kept.Add(face);
            var p = face.Points;
            if (p.Any(v => !float.IsFinite(v))) continue;
            var eyeX = (p[0] + p[2]) / 2; var eyeY = (p[1] + p[3]) / 2;
            var mouthX = (p[6] + p[8]) / 2; var mouthY = (p[7] + p[9]) / 2;
            var eyeDx = p[2] - p[0]; var eyeDy = p[3] - p[1];
            var eyeDistance = Math.Sqrt(eyeDx * eyeDx + eyeDy * eyeDy);
            var downX = mouthX - eyeX; var downY = mouthY - eyeY;
            var downDistance = Math.Sqrt(downX * downX + downY * downY);
            if (eyeDistance < 6 || downDistance < face.Height * .1 || downDistance > face.Height * .8) continue;
            var horizontal = Math.Abs(eyeDx) / eyeDistance; var upright = downY / downDistance;
            if (horizontal < .82 || upright < .82 || p[5] < eyeY - face.Height * .05 || p[5] > mouthY + face.Height * .05) continue;
            var areaWeight = Math.Sqrt(Math.Clamp(face.Width * face.Height / (size * size * .04), 0, 1));
            // Max caps each frame's influence, so crowd scenes cannot dominate the temporal vote.
            var geometryQuality = Math.Pow(horizontal * upright, 3);
            best = Math.Max(best, face.Confidence * geometryQuality * areaWeight);
        }
        return best;
    }

    private static double Overlap(Face a, Face b)
    {
        var width = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var height = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var intersection = width * height;
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }
}
