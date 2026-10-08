using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

/// <summary>Canonicalizes the pinned NanoDet graph's empty Resize inputs without changing weights.</summary>
internal static class PersonDetectionModel
{
    private sealed record Field(int Number, int Wire, byte[] Value);
    public static (byte[] Data, string Hash) Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var artifact = ModelCatalog.Find(ModelCatalog.NanoDetId).Files.Single();
        if (Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != artifact.Sha256)
            throw new InvalidDataException("NanoDet 模型校验失败。");
        var model = Fields(data);
        var graphField = model.Single(field => field.Number == 7);
        var graph = Fields(graphField.Value);
        var initializers = graph.Where(field => field.Number == 5)
            .Select(field => Text(Fields(field.Value).Single(item => item.Number == 8))).ToHashSet(StringComparer.Ordinal);
        // Exported weights were also listed as overridable inputs. Keep only actual image inputs.
        graph.RemoveAll(field => field.Number == 11 && initializers.Contains(Text(Fields(field.Value).Single(item => item.Number == 1))));
        var fixedNodes = 0;
        for (var index = 0; index < graph.Count; index++)
        {
            var field = graph[index];
            if (field.Number != 1) continue;
            var node = Fields(field.Value);
            var operation = Text(node.Single(item => item.Number == 4));
            var inputs = node.Select((item, position) => (item, position)).Where(pair => pair.item.Number == 1).ToArray();
            if (operation != "Resize") continue;
            if (inputs.Length != 4 || Text(inputs[1].item) != "703" || Text(inputs[2].item) != "703")
                throw new InvalidDataException("NanoDet Resize 输入格式无效。");
            // Both ROI and scales point at an empty tensor; ONNX requires omitted optional inputs
            // when the sizes input is present. Empty names are the canonical ONNX representation.
            node[inputs[1].position] = inputs[1].item with { Value = [] };
            node[inputs[2].position] = inputs[2].item with { Value = [] };
            graph[index] = field with { Value = Encode(node) }; fixedNodes++;
        }
        if (fixedNodes != 4) throw new InvalidDataException("人物检测模型节点数量无效。");
        model[model.IndexOf(graphField)] = graphField with { Value = Encode(graph) };
        var normalized = Encode(model);
        return (normalized, Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant());
    }
    private static string Text(Field field) => Encoding.UTF8.GetString(field.Value);
    private static List<Field> Fields(byte[] data)
    {
        var result = new List<Field>(); var offset = 0;
        while (offset < data.Length)
        {
            var tag = Varint(data, ref offset); var wire = (int)(tag & 7); var number = checked((int)(tag >> 3));
            if (number == 0) throw new InvalidDataException("NanoDet ONNX 字段无效。");
            var start = offset;
            var length = wire switch
            {
                0 => VarintLength(data, ref offset), 1 => 8,
                2 => checked((int)Varint(data, ref offset)), 5 => 4,
                _ => throw new InvalidDataException("NanoDet ONNX 字段类型无效。")
            };
            if (wire == 2) start = offset;
            if (wire != 0)
            {
                if (length < 0 || offset > data.Length - length) throw new InvalidDataException("NanoDet ONNX 字段长度无效。");
                offset += length;
            }
            result.Add(new(number, wire, data.AsSpan(start, length).ToArray()));
        }
        return result;
    }
    private static int VarintLength(byte[] data, ref int offset)
    { var start = offset; _ = Varint(data, ref offset); return offset - start; }
    private static ulong Varint(byte[] data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("NanoDet ONNX 字段不完整。");
            var next = data[offset++]; value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        throw new InvalidDataException("NanoDet ONNX 整数字段无效。");
    }
    private static byte[] Encode(IEnumerable<Field> fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            Write(stream, ((ulong)field.Number << 3) | (uint)field.Wire);
            if (field.Wire == 2) Write(stream, (ulong)field.Value.Length);
            stream.Write(field.Value);
        }
        return stream.ToArray();
    }
    private static void Write(Stream stream, ulong value)
    {
        while (value >= 128) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
    }
}
