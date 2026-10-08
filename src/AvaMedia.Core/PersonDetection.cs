using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AvaMedia.Core;

public enum PersonDetectionMode { Balanced, Recall, Consensus }
public sealed record PersonDetectorDefinition(string Id, string Name, int InputSize);
public sealed record PersonDetectionEvidence(string Id, double Score, double Threshold, string Backend);
public sealed record PersonDetectorStatistics(string Id, string Name, int Evaluations, int PositiveFrames, string Backend, string? FallbackReason);
public sealed record PersonDetectionDecision(bool Keep, bool Uncertain, int Votes);

public static class PersonDetectorCatalog
{
    public static IReadOnlyList<PersonDetectorDefinition> All { get; } = Array.AsReadOnly<PersonDetectorDefinition>([
        new(ModelCatalog.NanoDetId, "NanoDet", 416),
        new(ModelCatalog.MediaPipePersonId, "MediaPipe", 224),
        new(ModelCatalog.PersonId, "YOLOX", 640)
    ]);
    public static PersonDetectorDefinition Find(string id) => All.FirstOrDefault(model => model.Id == id)
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
        var votes = evidence.Count(item => item.Score >= item.Threshold);
        var required = evidence.Count == 1 ? 1 : Math.Max(2, (evidence.Count + 1) / 2);
        var positive = mode switch
        {
            PersonDetectionMode.Recall => votes > 0,
            PersonDetectionMode.Consensus => votes >= required,
            _ => votes >= required || evidence.Any(item => item.Score >= Math.Min(.98, item.Threshold + .2))
        };
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
        public int Evaluations, PositiveFrames;
    }
    private readonly List<Detector> _detectors = [];
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
            foreach (var id in options.SelectedDetectors.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var definition = PersonDetectorCatalog.Find(id);
                var lease = await store.AcquireAsync(id, ct);
                try
                {
                    var artifact = ModelCatalog.Find(id).Files.Single();
                    var path = Path.Combine(lease.Directory, artifact.Path);
                    var normalized = id == ModelCatalog.NanoDetId ? PersonDetectionModel.Read(path) : (Data: (byte[]?)null, Hash: artifact.Sha256);
                    var session = new ModelInferenceSession(path, normalized.Hash, options.PreferGpu, modelData: normalized.Data);
                    result._detectors.Add(new(definition, lease, session));
                }
                catch { lease.Dispose(); throw; }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public PersonDetectionEvidence[] Detect(byte[] rgb, double threshold, CancellationToken ct)
    {
        var evidence = new List<PersonDetectionEvidence>();
        foreach (var detector in _detectors)
        {
            ct.ThrowIfCancellationRequested();
            var definition = detector.Definition;
            var tensor = Preprocess(rgb, FrameSize, definition);
            using var output = detector.Session.Run(NamedOnnxValue.CreateFromTensor(detector.Session.InputName, tensor), ct);
            ct.ThrowIfCancellationRequested();
            double score = 0;
            if (definition.Id == ModelCatalog.MediaPipePersonId)
            {
                var scores = output.Select(value => value.AsTensor<float>()).SingleOrDefault(value => value.Rank == 3 && value.Dimensions[2] == 1)
                    ?? throw new InvalidDataException("MediaPipe 人体检测模型输出格式无效。");
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
            if (!double.IsFinite(score) || score is < 0 or > 1) throw new InvalidDataException("人物检测返回了无效分数。");
            // Use a conservative .6 baseline for MediaPipe; YOLOX and NanoDet use .35.
            var cutoff = definition.Id == ModelCatalog.MediaPipePersonId ? Math.Min(.98, threshold * .6 / .35) : threshold;
            detector.Evaluations++;
            if (score >= cutoff) detector.PositiveFrames++;
            evidence.Add(new(definition.Id, score, cutoff, detector.Session.Backend));
        }
        return evidence.ToArray();
    }

    private static DenseTensor<float> Preprocess(byte[] rgb, int sourceSize, PersonDetectorDefinition definition)
    {
        var size = definition.InputSize;
        if (rgb.Length != sourceSize * sourceSize * 3) throw new InvalidDataException("人物检测画面尺寸无效。");
        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
        ReadOnlySpan<float> mean = [103.53f, 116.28f, 123.675f], std = [57.375f, 57.12f, 58.395f];
        var plane = size * size;
        var pixels = tensor.Buffer.Span;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var sx = Math.Clamp((x + .5) * sourceSize / size - .5, 0, sourceSize - 1);
                var sy = Math.Clamp((y + .5) * sourceSize / size - .5, 0, sourceSize - 1);
                var x0 = (int)sx; var y0 = (int)sy;
                var x1 = Math.Min(x0 + 1, sourceSize - 1); var y1 = Math.Min(y0 + 1, sourceSize - 1);
                var dx = (float)(sx - x0); var dy = (float)(sy - y0);
                for (var channel = 0; channel < 3; channel++)
                {
                    var c = definition.Id == ModelCatalog.NanoDetId ? 2 - channel : channel;
                    var top = rgb[(y0 * sourceSize + x0) * 3 + c] * (1 - dx) + rgb[(y0 * sourceSize + x1) * 3 + c] * dx;
                    var bottom = rgb[(y1 * sourceSize + x0) * 3 + c] * (1 - dx) + rgb[(y1 * sourceSize + x1) * 3 + c] * dx;
                    var value = top * (1 - dy) + bottom * dy;
                    pixels[channel * plane + y * size + x] = definition.Id switch
                    {
                        ModelCatalog.NanoDetId => (value - mean[channel]) / std[channel],
                        ModelCatalog.MediaPipePersonId => value / 127.5f - 1,
                        _ => value
                    };
                }
            }
        return tensor;
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
