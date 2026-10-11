using System.ComponentModel;
using System.Text.Json;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpEditorTrack(string Kind, int Index, string Label);
public sealed record McpEditorMedia(string SourceVersion, long Bytes, MediaInfo Info, McpEditorTrack[] Tracks);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_editor_media", Title = "编辑器媒体与轨道", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read actual video/audio/subtitle tracks and source metadata for visual editors. Track indexes use the conversion engine's per-type indexing.")]
    public async Task<McpEditorMedia> EditorMedia(string path, CancellationToken ct = default)
    {
        path = _files.Check(path, true); var version = McpMediaFile.Version(path);
        var info = await workspace.Engine.Probe(path, ct).ConfigureAwait(false);
        using var raw = JsonDocument.Parse(info.RawJson);
        var tracks = new List<McpEditorTrack>();
        foreach (var kind in new[] { "video", "audio", "subtitle" })
        {
            var index = 0;
            foreach (var stream in raw.RootElement.GetProperty("streams").EnumerateArray().Where(stream => Text(stream, "codec_type") == kind))
            {
                var trackIndex = index++;
                if (kind == "video" && !MediaStreams.IsContentVideo(stream)) continue;
                var label = $"轨道 {trackIndex + 1} · {Text(stream, "codec_name")}";
                if (stream.TryGetProperty("tags", out var tags))
                    label += $" · {Text(tags, "language")} {Text(tags, "title")}".TrimEnd();
                if (kind == "video") label += $" · {Text(stream, "width")}×{Text(stream, "height")}";
                if (kind == "audio") label += $" · {Text(stream, "sample_rate")} Hz · {Text(stream, "channels")} 声道";
                tracks.Add(new(kind, trackIndex, label));
            }
        }
        if (version != McpMediaFile.Version(path)) throw new IOException("读取期间源文件发生变化，请重试。");
        return new(version, new FileInfo(path).Length, info with { RawJson = "" }, tracks.ToArray());
        static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) ? field.ToString() : "";
    }

    [McpServerTool(Name = "avamedia_preview_compression", Title = "视频压缩计划", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Estimate a video compression plan using the desktop and queue planner. Quality mode intentionally has no predicted size. Does not encode media or enqueue a task.")]
    public async Task<VideoCompressionPlan> PreviewCompression(string path, VideoCompressionOptions options, CancellationToken ct = default)
    {
        path = _files.Check(path, true); var version = McpMediaFile.Version(path);
        var info = await workspace.Engine.Probe(path, ct).ConfigureAwait(false);
        var plan = VideoCompression.Plan(new FileInfo(path).Length, info, options);
        if (version != McpMediaFile.Version(path)) throw new IOException("读取期间源文件发生变化，请重试。");
        return plan;
    }
}
