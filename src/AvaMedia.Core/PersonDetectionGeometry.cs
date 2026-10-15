using Microsoft.ML.OnnxRuntime.Tensors;

namespace AvaMedia.Core;

/// <summary>Decode the pinned OpenCV Zoo NanoDet/YOLOX geometry, without changing classification policy.
/// References: https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_nanodet
/// https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox
/// https://github.com/opencv/opencv_zoo/tree/main/models/person_detection_mediapipe
/// MediaPipe boxes use the detector's observed head region, not an inferred full-body extent.</summary>
internal static class PersonDetectionGeometry
{
    internal static IReadOnlyList<PersonClipDetectionBox> Read(PersonDetectorDefinition model, Tensor<float>[] outputs, double threshold)
    {
        List<PersonClipDetectionBox> boxes = [];
        if (model.Id == ModelCatalog.NanoDetId)
        {
            foreach (var scores in outputs.Where(t => t.Rank == 3 && t.Dimensions[2] == 80))
            {
                var count = scores.Dimensions[1];
                var stride = new[] { 8, 16, 32, 64 }.FirstOrDefault(s => (model.InputSize / s) * (model.InputSize / s) == count);
                var distances = outputs.FirstOrDefault(t => t.Rank == 3 && t.Dimensions[1] == count && t.Dimensions[2] == 32);
                if (stride == 0 || distances is null) continue;
                var width = model.InputSize / stride;
                for (var i = 0; i < count; i++)
                {
                    var confidence = scores[0, i, 0];
                    if (confidence < threshold || Enumerable.Range(1, 79).Any(c => scores[0, i, c] > confidence)) continue;
                    var cx = i % width * stride + .5 * (stride - 1);
                    var cy = i / width * stride + .5 * (stride - 1);
                    double Distance(int side)
                    {
                        double maximum = double.NegativeInfinity;
                        for (var bin = 0; bin < 8; bin++) maximum = Math.Max(maximum, distances[0, i, side * 8 + bin]);
                        double sum = 0, weighted = 0;
                        for (var bin = 0; bin < 8; bin++)
                        { var value = Math.Exp(distances[0, i, side * 8 + bin] - maximum); sum += value; weighted += bin * value; }
                        return weighted / sum * stride;
                    }
                    Add(cx - Distance(0), cy - Distance(1), cx + Distance(2), cy + Distance(3), confidence);
                }
            }
        }
        else if (model.Id == ModelCatalog.PersonId)
        {
            var predictions = outputs.FirstOrDefault(t => t.Rank == 3 && t.Dimensions[2] == 85);
            if (predictions is not null)
            {
                var index = 0;
                foreach (var stride in new[] { 8, 16, 32 })
                {
                    var width = model.InputSize / stride;
                    for (var y = 0; y < width; y++)
                        for (var x = 0; x < width; x++, index++)
                        {
                            if (index >= predictions.Dimensions[1]) return [];
                            var score = predictions[0, index, 5] * predictions[0, index, 4];
                            if (score < threshold) continue;
                            var person = true;
                            for (var cls = 6; cls < 85; cls++) if (predictions[0, index, cls] > predictions[0, index, 5]) { person = false; break; }
                            if (!person) continue;
                            var cx = (predictions[0, index, 0] + x) * stride;
                            var cy = (predictions[0, index, 1] + y) * stride;
                            var w = Math.Exp(predictions[0, index, 2]) * stride;
                            var h = Math.Exp(predictions[0, index, 3]) * stride;
                            Add(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, score);
                        }
                }
            }
        }
        else if (model.Id == ModelCatalog.MediaPipePersonId)
        {
            var scores = outputs.FirstOrDefault(t => t.Rank == 3 && t.Dimensions[2] == 1);
            var deltas = outputs.FirstOrDefault(t => t.Rank == 3 && t.Dimensions[2] == 12);
            // The pinned BlazePose person detector: stride 8 x 2, stride 16 x 2, stride 32 x 6.
            if (scores?.Dimensions[1] == 2254 && deltas?.Dimensions[1] == 2254)
            {
                var index = 0;
                foreach (var (width, repeats) in new[] { (28, 2), (14, 2), (7, 6) })
                    for (var y = 0; y < width; y++)
                        for (var x = 0; x < width; x++)
                            for (var anchor = 0; anchor < repeats; anchor++, index++)
                            {
                                var confidence = 1 / (1 + Math.Exp(-Math.Clamp((double)scores[0, index, 0], -100, 100)));
                                if (confidence < threshold) continue;
                                var cx = deltas[0, index, 0] + (x + .5) / width * model.InputSize;
                                var cy = deltas[0, index, 1] + (y + .5) / width * model.InputSize;
                                var w = deltas[0, index, 2]; var h = deltas[0, index, 3];
                                Add(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, confidence);
                            }
            }
        }
        return Suppress(boxes, model.Id switch { ModelCatalog.NanoDetId => .6, ModelCatalog.MediaPipePersonId => .3, _ => .5 });
        void Add(double left, double top, double right, double bottom, double confidence)
        {
            if (!new[] { left, top, right, bottom, confidence }.All(double.IsFinite)) return;
            left = Math.Clamp(left / model.InputSize, 0, 1); top = Math.Clamp(top / model.InputSize, 0, 1);
            right = Math.Clamp(right / model.InputSize, 0, 1); bottom = Math.Clamp(bottom / model.InputSize, 0, 1);
            if (right > left && bottom > top) boxes.Add(new(left, top, right - left, bottom - top, confidence));
        }
    }
    internal static IReadOnlyList<PersonClipDetectionBox> Suppress(IEnumerable<PersonClipDetectionBox> boxes, double threshold)
    {
        List<PersonClipDetectionBox> kept = [];
        foreach (var box in boxes.OrderByDescending(b => b.Confidence))
        {
            if (kept.Any(other =>
            {
                var intersection = Math.Max(0, Math.Min(box.X + box.Width, other.X + other.Width) - Math.Max(box.X, other.X))
                    * Math.Max(0, Math.Min(box.Y + box.Height, other.Y + other.Height) - Math.Max(box.Y, other.Y));
                return intersection / (box.Width * box.Height + other.Width * other.Height - intersection) > threshold;
            })) continue;
            kept.Add(box);
        }
        return kept.ToArray();
    }
    internal static IReadOnlyList<PersonClipDetectionBox> ToPreview(IReadOnlyList<PersonClipDetectionBox> boxes,
        PersonDetectorDefinition model, (int Left, int Top, int Width, int Height) content)
    {
        var scale = model.InputSize / (double)Math.Max(content.Width, content.Height);
        var width = Math.Clamp((int)Math.Round(content.Width * scale), 1, model.InputSize);
        var height = Math.Clamp((int)Math.Round(content.Height * scale), 1, model.InputSize);
        var left = model.Id == ModelCatalog.PersonId ? 0 : (model.InputSize - width) / 2;
        var top = model.Id == ModelCatalog.PersonId ? 0 : (model.InputSize - height) / 2;
        return boxes.Select(box =>
        {
            double X(double x) => Math.Clamp((x * model.InputSize - left) / width, 0, 1);
            double Y(double y) => Math.Clamp((y * model.InputSize - top) / height, 0, 1);
            var x = X(box.X); var y = Y(box.Y);
            return box with { X = x, Y = y, Width = Math.Max(0, X(box.X + box.Width) - x), Height = Math.Max(0, Y(box.Y + box.Height) - y) };
        }).Where(box => box.Width > 0 && box.Height > 0).ToArray();
    }
}
