using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

/// <summary>Flat [milliseconds, score × 1000, flags, …]; millisecond / .001 precision on disk.</summary>
public sealed class PersonClipSamplesJsonConverter : JsonConverter<IReadOnlyList<PersonClipSample>>
{
    public override IReadOnlyList<PersonClipSample> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("人物采样数据须为数组。");
        var samples = new List<PersonClipSample>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var milliseconds) || milliseconds < 0
                || !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var score) || score is < 0 or > 1000
                || !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var flags) || flags is < 0 or > 127)
                throw new JsonException("人物采样数据无效。");
            samples.Add(new(milliseconds / 1000d, score / 1000f, (PersonClipSampleFlags)flags));
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("人物采样数据不完整。");
        return samples.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<PersonClipSample> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var sample in value)
        {
            if (!double.IsFinite(sample.Seconds) || sample.Seconds < 0 || sample.Seconds > long.MaxValue / 1000d
                || !float.IsFinite(sample.Score) || sample.Score is < 0 or > 1 || (int)sample.Flags > 127)
                throw new JsonException("人物采样数据无效。");
            writer.WriteNumberValue(checked((long)Math.Round(sample.Seconds * 1000)));
            writer.WriteNumberValue((int)Math.Round(sample.Score * 1000));
            writer.WriteNumberValue((int)sample.Flags);
        }
        writer.WriteEndArray();
    }
}
