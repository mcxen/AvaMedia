using System.Globalization;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

/// <summary>Convert normalized preview coordinates into the subtitle script's coordinate space.</summary>
internal sealed class SubtitlePositioning : IDisposable
{
    private readonly List<string> _temporary = [];
    public static string Tag(ConversionOptions options, double width, double height) =>
        options.SubtitlePositionX is {} x && options.SubtitlePositionY is {} y
            ? string.Create(CultureInfo.InvariantCulture, $"{{\\an5\\pos({x * width:0.###},{y * height:0.###})}}") : "";

    public static async Task<SubtitlePositioning> PrepareAsync(IMediaEngine engine, Job job, CancellationToken ct)
    {
        var prepared = new SubtitlePositioning();
        try
        {
            await Prepare(job.Options, job.Inputs[0]);
            if (job.InputOptions is not null)
                for (var index = 0; index < job.InputOptions.Count; index++) await Prepare(job.InputOptions[index], job.Inputs[index]);
            return prepared;
        }
        catch { prepared.Dispose(); throw; }

        async Task Prepare(ConversionOptions options, string source)
        {
            if (SubtitleOptions.Mode(options) != SubtitleMode.BurnIn || options.SubtitlePositionX is null) return;
            var path = Path.Combine(Path.GetTempPath(), "AvaMedia-subtitle-position-" + Guid.NewGuid().ToString("N") + ".ass");
            prepared._temporary.Add(path);
            var result = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-nostdin", "-n", "-i",
                string.IsNullOrWhiteSpace(options.Subtitle) ? source : options.Subtitle,
                "-map", $"0:s:{Math.Max(0, options.SubtitleStreamIndex)}", "-c:s", "ass", path], ct).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidDataException("准备字幕位置失败。\n" + result.Error);
            var script = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var width = Resolution("PlayResX", 384); var height = Resolution("PlayResY", 288);
            var tag = Tag(options, width, height);
            var lines = script.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].StartsWith("Dialogue:", StringComparison.Ordinal)) continue;
                var fields = lines[index].Split(',', 10);
                if (fields.Length != 10) continue;
                // Explicit placement replaces existing alignment and motion; other cue styling survives.
                fields[9] = tag + Regex.Replace(fields[9], @"\{[^}]*\}", block =>
                    Regex.Replace(block.Value, @"\\(?:pos|move)\([^)]*\)|\\an\d+|\\a\d+", ""));
                lines[index] = string.Join(',', fields);
            }
            await File.WriteAllTextAsync(path, string.Join('\n', lines), ct).ConfigureAwait(false);
            options.Subtitle = path; options.SubtitleStreamIndex = 0;
            double Resolution(string name, double fallback)
            {
                var match = Regex.Match(script, @"(?m)^" + name + @":\s*(\d+)");
                return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
            }
        }
    }

    public void Dispose()
    {
        foreach (var path in _temporary) if (File.Exists(path)) File.Delete(path);
    }
}
