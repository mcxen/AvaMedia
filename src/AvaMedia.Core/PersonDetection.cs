using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AvaMedia.Core;

public enum PersonDetectionMode { Balanced, Recall, Consensus }
public sealed record PersonDetectorDefinition(string Id, string Name, int InputSize);
public sealed record PersonDetectionEvidence(string Id, double Score, double Threshold, string Backend)
{
    public IReadOnlyList<PersonClipDetectionBox> Boxes { get; init; } = [];
}
public sealed record PersonDetectorStatistics(string Id, string Name, int Evaluations, int PositiveFrames, string Backend, string? FallbackReason);
public sealed record PersonDetectionDecision(bool Keep, bool Uncertain, int Votes);

public static class PersonDetectorCatalog
{
    public static IReadOnlyList<PersonDetectorDefinition> All { get; } = Array.AsReadOnly<PersonDetectorDefinition>([
        new(ModelCatalog.NanoDetId, "NanoDet", 416),
        new(ModelCatalog.MediaPipePersonId, "MediaPipe", 224),
        new(ModelCatalog.PersonId, "YOLOX", 640)
    ]);
    public static PersonDetectorDefinition Hands { get; } = new(ModelCatalog.MediaPipePalmId, "手部特写", 192);
    public static PersonDetectorDefinition Find(string id) => (id == Hands.Id ? Hands : All.FirstOrDefault(model => model.Id == id))
        ?? throw new ArgumentException("请选择有效的人物检测模型。");
}

/// <summary>Model scores are separate evidence strengths, never averaged as calibrated probabilities.</summary>
public static class PersonDetectionPolicy
{
    public static PersonDetectionDecision Decide(IReadOnlyList<PersonDetectionEvidence> evidence, PersonDetectionMode mode, bool keepUncertain)
    {
        if (evidence.Count == 0 || !Enum.IsDefined(mode) || evidence.Select(item => item.Id).Distinct().Count() != evidence.Count
            || evidence.Any(item => !double.IsFinite(item.Score) || item.Score is < 0 or > 1
                || !double.IsFinite(item.Threshold) || item.Threshold is <= 0 or > 1))
            throw new ArgumentException("人物检测证据无效。");
        var bodies = evidence.Where(item => item.Id != ModelCatalog.MediaPipePalmId).ToArray();
        var votes = bodies.Count(item => item.Score >= item.Threshold);
        var required = bodies.Length == 1 ? 1 : Math.Max(2, (bodies.Length + 1) / 2);
        var positive = mode switch
        {
            PersonDetectionMode.Recall => votes > 0,
            PersonDetectionMode.Consensus => votes >= required,
            _ => votes >= required || bodies.Any(item => item.Score >= Math.Min(.98, item.Threshold + .2))
        };
        positive |= evidence.Any(item => item.Id == ModelCatalog.MediaPipePalmId && item.Score >= item.Threshold);
        var uncertain = !positive && evidence.Any(item => item.Score >= item.Threshold * .5);
        return new(positive || keepUncertain && uncertain, uncertain, votes);
    }
}

/// <summary>Every selected detector sees the same decoded frame; leases and sessions live for one analysis.</summary>
internal sealed class PersonDetectorSet : IDisposable
{
    private sealed class Detector(PersonDetectorDefinition definition, ModelLease lease, ModelInferenceSession session)
    {
        public PersonDetectorDefinition Definition { get; } = definition;
        public ModelLease Lease { get; } = lease;
        public ModelInferenceSession Session { get; } = session;
        public DenseTensor<float> Input { get; } = new(definition.Id == ModelCatalog.MediaPipePalmId
            ? new[] { 1, definition.InputSize, definition.InputSize, 3 } : new[] { 1, 3, definition.InputSize, definition.InputSize });
        public int Evaluations, PositiveFrames;
        public IReadOnlyList<PersonClipDetectionBox> Boxes = [];
    }
    private readonly List<Detector> _detectors = [];
    private (int Left, int Top, int Width, int Height) _content;
    public int FrameSize => _detectors.Max(detector => detector.Definition.InputSize);
    public string Name => string.Join(" + ", _detectors.Select(detector => detector.Definition.Name));
    public string Backend => string.Join(" · ", _detectors.Select(detector => detector.Definition.Name + ": " + detector.Session.Backend));
    public PersonDetectorStatistics[] Statistics => _detectors.Select(detector => new PersonDetectorStatistics(
        detector.Definition.Id, detector.Definition.Name, detector.Evaluations, detector.PositiveFrames,
        detector.Session.Backend, detector.Session.FallbackReason)).ToArray();

    public static async Task<PersonDetectorSet> CreateAsync(ModelStore store, PersonClipOptions options, CancellationToken ct)
    {
        var result = new PersonDetectorSet();
        try
        {
            foreach (var id in options.RequiredModels.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var definition = PersonDetectorCatalog.Find(id);
                var lease = await store.AcquireAsync(id, ct);
                try
                {
                    var artifact = ModelCatalog.Find(id).Files.Single();
                    var path = Path.Combine(lease.Directory, artifact.Path);
                    var normalized = id == ModelCatalog.NanoDetId ? PersonDetectionModel.Read(path) : (Data: (byte[]?)null, Hash: artifact.Sha256);
                    var session = new ModelInferenceSession(path, normalized.Hash, modelData: normalized.Data);
                    result._detectors.Add(new(definition, lease, session));
                }
                catch { lease.Dispose(); throw; }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void SetFrameGeometry(MediaInfo info)
    {
        var scale = FrameSize / (double)Math.Max(info.Width, info.Height);
        var width = Math.Clamp((int)Math.Round(info.Width * scale), 1, FrameSize);
        var height = Math.Clamp((int)Math.Round(info.Height * scale), 1, FrameSize);
        _content = ((FrameSize - width) / 2, (FrameSize - height) / 2, width, height);
    }

    public PersonDetectionEvidence[] Detect(byte[] rgb, double threshold, CancellationToken ct) =>
        _detectors.Where(detector => detector.Definition.Id != ModelCatalog.MediaPipePalmId)
            .Select(detector => Detect(detector, rgb, threshold, ct)).ToArray();

    public PersonDetectionEvidence DetectHands(byte[] rgb, CancellationToken ct) =>
        Detect(_detectors.Single(detector => detector.Definition.Id == ModelCatalog.MediaPipePalmId), rgb, .5, ct);

    private PersonDetectionEvidence Detect(Detector detector, byte[] rgb, double threshold, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var definition = detector.Definition;
        var cutoff = definition.Id == ModelCatalog.MediaPipePersonId ? Math.Min(.98, threshold * .6 / .35) : threshold;
        Preprocess(rgb, FrameSize, _content, definition, detector.Input);
        double score;
        try { score = Score(detector, cutoff, ct); }
        catch (InvalidDataException error) when (detector.Session.Backend != "CPU" && !ct.IsCancellationRequested)
        {
            // A provider may complete successfully while returning NaN/Inf or invalid probabilities.
            // Validate the output as well as runtime exceptions before trusting accelerated inference.
            detector.Session.FallbackToCpu($"{definition.Name} 输出无效：{error.Message}");
            score = Score(detector, cutoff, ct);
        }
        detector.Evaluations++;
        if (score >= cutoff) detector.PositiveFrames++;
        return new(definition.Id, score, cutoff, detector.Session.Backend)
        { Boxes = PersonDetectionGeometry.ToPreview(detector.Boxes, definition, FrameSize, _content) };
    }

    private static double Score(Detector detector, double threshold, CancellationToken ct)
    {
        var definition = detector.Definition;
        var input = NamedOnnxValue.CreateFromTensor(detector.Session.InputName, detector.Input);
        using var output = detector.Session.Run(input, ct);
        ct.ThrowIfCancellationRequested();
        double score = 0;
        if (definition.Id is ModelCatalog.MediaPipePersonId or ModelCatalog.MediaPipePalmId)
        {
            var scores = output.Select(value => value.AsTensor<float>()).SingleOrDefault(value => value.Rank == 3 && value.Dimensions[2] == 1)
                ?? throw new InvalidDataException("MediaPipe 检测模型输出格式无效。");
            foreach (var logit in scores)
            {
                if (!float.IsFinite(logit)) throw new InvalidDataException("人物检测返回了无效分数。");
                score = Math.Max(score, 1 / (1 + Math.Exp(-Math.Clamp((double)logit, -100, 100))));
            }
        }
        else
        {
            var width = definition.Id == ModelCatalog.PersonId ? 85 : 80;
            var scores = output.Select(value => value.AsTensor<float>()).Where(value => value.Rank == 3 && value.Dimensions[2] == width).ToArray();
            if (scores.Length == 0) throw new InvalidDataException("人物检测模型输出格式无效。");
            foreach (var predictions in scores)
            {
                if (predictions is DenseTensor<float> dense)
                {
                    score = Math.Max(score, PersonScore(dense.Buffer.Span, predictions.Dimensions[1], width));
                    continue;
                }
                for (var index = 0; index < predictions.Dimensions[1]; index++)
                {
                    var offset = width == 85 ? 5 : 0;
                    var person = predictions[0, index, offset];
                    var bestClass = true;
                    for (var cls = offset + 1; cls < width; cls++)
                        if (predictions[0, index, cls] > person) { bestClass = false; break; }
                    if (bestClass) score = Math.Max(score, person * (width == 85 ? predictions[0, index, 4] : 1));
                }
            }
        }
        if (!double.IsFinite(score) || score is < 0 or > 1) throw new InvalidDataException("人物检测返回了无效分数。");
        detector.Boxes = PersonDetectionGeometry.Read(definition, output.Select(value => value.AsTensor<float>()).ToArray(), threshold);
        return score;
    }

    private static double PersonScore(ReadOnlySpan<float> predictions, int count, int width)
    {
        double score = 0;
        var offset = width == 85 ? 5 : 0;
        for (var index = 0; index < count; index++)
        {
            var row = predictions.Slice(index * width, width);
            var person = row[offset];
            var candidate = person * (width == 85 ? row[4] : 1);
            if (candidate <= score) continue;
            var bestClass = true;
            for (var cls = offset + 1; cls < width; cls++)
                if (row[cls] > person) { bestClass = false; break; }
            if (bestClass) score = Math.Max(score, candidate);
        }
        return score;
    }

    private static void Preprocess(byte[] rgb, int sourceSize, (int Left, int Top, int Width, int Height) content,
        PersonDetectorDefinition definition, DenseTensor<float> tensor)
    {
        if (rgb.Length != sourceSize * sourceSize * 3 || content.Width < 1 || content.Height < 1)
            throw new InvalidDataException("人物检测画面尺寸无效。");
        var size = definition.InputSize;
        var scale = size / (double)Math.Max(content.Width, content.Height);
        var width = Math.Clamp((int)Math.Round(content.Width * scale), 1, size);
        var height = Math.Clamp((int)Math.Round(content.Height * scale), 1, size);
        // Keep NanoDet's trained BGR normalization with black padding;
        // MediaPipe RGB [-1,1] with zero padding, YOLOX RGB [0,255] placed at the top-left.
        var left = definition.Id == ModelCatalog.PersonId ? 0 : (size - width) / 2;
        var top = definition.Id == ModelCatalog.PersonId ? 0 : (size - height) / 2;
        ReadOnlySpan<float> mean = [103.53f, 116.28f, 123.675f], std = [57.375f, 57.12f, 58.395f];
        var plane = size * size;
        var pixels = tensor.Buffer.Span;
        for (var channel = 0; channel < 3; channel++)
            pixels.Slice(channel * plane, plane).Fill(definition.Id switch
            {
                ModelCatalog.NanoDetId => -mean[channel] / std[channel],
                ModelCatalog.MediaPipePersonId or ModelCatalog.MediaPipePalmId => 0,
                _ => 114
            });
        var exactSize = width == content.Width && height == content.Height;
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + .5) * content.Height / height - .5, 0, content.Height - 1);
            var y0 = (int)sy; var y1 = Math.Min(y0 + 1, content.Height - 1); var dy = (float)(sy - y0);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + .5) * content.Width / width - .5, 0, content.Width - 1);
                var x0 = (int)sx; var x1 = Math.Min(x0 + 1, content.Width - 1); var dx = (float)(sx - x0);
                var destination = (top + y) * size + left + x;
                for (var channel = 0; channel < 3; channel++)
                {
                    float value;
                    var sourceChannel = definition.Id == ModelCatalog.NanoDetId ? 2 - channel : channel;
                    if (exactSize) value = rgb[((content.Top + y) * sourceSize + content.Left + x) * 3 + sourceChannel];
                    else
                    {
                        var a = rgb[((content.Top + y0) * sourceSize + content.Left + x0) * 3 + sourceChannel];
                        var b = rgb[((content.Top + y0) * sourceSize + content.Left + x1) * 3 + sourceChannel];
                        var c = rgb[((content.Top + y1) * sourceSize + content.Left + x0) * 3 + sourceChannel];
                        var d = rgb[((content.Top + y1) * sourceSize + content.Left + x1) * 3 + sourceChannel];
                        value = (a * (1 - dx) + b * dx) * (1 - dy) + (c * (1 - dx) + d * dx) * dy;
                    }
                    var offset = definition.Id == ModelCatalog.MediaPipePalmId ? destination * 3 + channel : channel * plane + destination;
                    pixels[offset] = definition.Id switch
                    {
                        ModelCatalog.NanoDetId => (value - mean[channel]) / std[channel],
                        ModelCatalog.MediaPipePersonId => value / 127.5f - 1,
                        ModelCatalog.MediaPipePalmId => value / 255f,
                        _ => value
                    };
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var detector in _detectors)
        {
            try { detector.Session.Dispose(); }
            finally { detector.Lease.Dispose(); }
        }
        _detectors.Clear();
    }
}
