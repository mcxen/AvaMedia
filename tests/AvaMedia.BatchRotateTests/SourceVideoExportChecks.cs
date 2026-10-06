using System.Security.Cryptography;
using System.Text.Json;
using AvaMedia.Core;

internal static class SourceVideoExportChecks
{
    public static async Task<IReadOnlyList<string>> Run(MediaEngine engine, string root, string mp4, string tagged, Action<bool, string> check)
    {
        var outputs = new List<string>();
        var subtitle = Path.Combine(root, "原字幕.srt");
        await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:01,000\nOriginal subtitle\n");
        var chapters = Path.Combine(root, "chapters.txt");
        await File.WriteAllTextAsync(chapters, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1200\ntitle=Original chapter\n");
        var mov = Path.Combine(root, "原属性 双音轨字幕.mov");
        var fixture = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-i", mp4,
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-i", subtitle, "-i", chapters,
            "-map", "0:v", "-map", "0:a", "-map", "1:a", "-map", "2:s", "-map_chapters", "3", "-t", "1.2",
            "-c:v", "copy", "-c:a", "pcm_s16le", "-c:s", "mov_text", "-metadata", "title=Original title",
            "-metadata", "comment=Original comment", "-metadata:s:a:1", "language=eng", mov]);
        check(fixture.ExitCode == 0, "Source MOV fixture failed: " + fixture.Error);
        var prores = Path.Combine(root, "10位 ProRes HQ.mov");
        fixture = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-i", mp4,
            "-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv", "-c:a", "pcm_s24le", "-ar", "48000", "-ac", "2", prores]);
        check(fixture.ExitCode == 0, "ProRes fixture failed: " + fixture.Error);
        var taggedProres = Path.Combine(root, "10位 ProRes 原方向.mov");
        fixture = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-display_rotation", "90", "-i", prores, "-c", "copy", taggedProres]);
        check(fixture.ExitCode == 0, "Tagged ProRes fixture failed: " + fixture.Error);
        var sources = new[] { mp4, tagged, mov, prores, taggedProres };
        var infos = new Dictionary<string, MediaInfo>();
        foreach (var source in sources) infos[source] = await engine.Probe(source);
        var hashes = sources.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        foreach (var angle in new[] { 90, 180, 270 })
        {
            var jobs = BatchRotate.CreateJobs(new(sources.Select(p => new BatchRotateInput(p, infos[p])).ToArray(), angle,
                SourceVideoExport.FastRotation, Path.Combine(root, "fast-" + angle)));
            await new QueueService(engine).Run(jobs, 2);
            foreach (var job in jobs)
            {
                check(job.State == JobState.Completed, "Fast Copy rotation failed: " + job.Error);
                outputs.Add(job.Output);
                var source = infos[job.Inputs[0]]; var result = await engine.Probe(job.Output);
                check((result.Width, result.Height) == BatchRotate.OutputSize(source, angle), "Fast Copy display geometry is wrong.");
                check(await Packets(job.Inputs[0], "v") == await Packets(job.Output, "v") && await Packets(job.Inputs[0], "a") == await Packets(job.Output, "a"), "Fast Copy changed video/audio payloads: " + job.Inputs[0]);
                await ComparePixels(job.Inputs[0], job.Output, source, result, angle);
                if (job.Inputs[0] == mov) await CheckTracks(job.Output);
            }
        }
        var originalJobs = BatchRotate.CreateJobs(new(sources.Select(p => new BatchRotateInput(p, infos[p])).ToArray(), 90,
            SourceVideoExport.Original, Path.Combine(root, "source-rotate")));
        await new QueueService(engine).Run(originalJobs, 2);
        foreach (var job in originalJobs)
        {
            check(job.State == JobState.Completed, "Original-attribute rotation failed: " + job.Error); outputs.Add(job.Output);
            var source = infos[job.Inputs[0]]; var output = await engine.Probe(job.Output);
            check(Path.GetExtension(job.Output) == Path.GetExtension(job.Inputs[0]) && output.VideoCodec == source.VideoCodec,
                "Original export changed container or video codec.");
            check((output.Width, output.Height) == BatchRotate.OutputSize(source, 90) && Math.Abs(output.Duration - source.Duration) < .15, "Original export geometry/duration differs: " + job.Inputs[0]);
            check(await Packets(job.Inputs[0], "a") == await Packets(job.Output, "a"), "Original export reencoded/lost audio.");
            await CheckVideoAttributes(job.Inputs[0], job.Output);
            await ComparePixels(job.Inputs[0], job.Output, source, output, 90);
            if (job.Inputs[0] == mov) await CheckTracks(job.Output);
            var persisted = JsonSerializer.Deserialize<ConversionOptions>(JsonSerializer.Serialize(job.Options))!;
            check(persisted.PreserveSourceAttributes, "Original export mode was lost during queue persistence.");
        }
        foreach (var source in new[] { mov, prores })
        {
            var info = infos[source];
            var job = BatchCrop.CreateJobs(new([new(source, info)], new(20, 10, 160, 100), info, BatchCropMode.Pixels,
                new() { Format = SourceVideoExport.Original }, Path.Combine(root, "source-crop"))).Single();
            await engine.Execute(job, _ => { }, CancellationToken.None); outputs.Add(job.Output);
            var output = await engine.Probe(job.Output);
            check(output.Width == 160 && output.Height == 100 && output.VideoCodec == info.VideoCodec && job.Output.EndsWith(".mov"), "Original MOV crop lost container, codec or geometry.");
            await CheckVideoAttributes(source, job.Output);
            check(await Packets(source, "a") == await Packets(job.Output, "a"), "Crop reencoded/lost audio.");
            if (source == mov) await CheckTracks(job.Output);
        }
        var fastOptions = originalJobs[0].Options.Clone(); fastOptions.PreserveSourceAttributes = false; fastOptions.CopyStreams = true; fastOptions.Rotation = 0; fastOptions.LosslessRotation = 90;
        check(JsonSerializer.Deserialize<ConversionOptions>(JsonSerializer.Serialize(fastOptions))!.LosslessRotation == 90, "Fast Copy mode was lost during queue persistence.");
        Reject(() => SourceVideoExport.ValidateOptions(WithRotation()), "Fast Copy accepted pixel rotation.");
        var wrongFormat = Path.Combine(root, "unsupported.mkv"); File.Copy(mp4, wrongFormat);
        var invalidFolder = Path.Combine(root, "fast-invalid");
        Reject(() => BatchRotate.CreateJobs(new([new(wrongFormat, infos[mp4])], 90, SourceVideoExport.FastRotation, invalidFolder)), "Fast Copy accepted unsupported container.");
        check(!Directory.Exists(invalidFolder), "Invalid Fast Copy created output folder.");
        Reject(() => SourceVideoExport.Encoder(infos[prores], " V..... mpeg4 encoder"), "Original export silently switched codec.");
        check(sources.All(p => hashes[p].SequenceEqual(SHA256.HashData(File.ReadAllBytes(p)))), "Source export modified original files.");
        return outputs;

        async Task<string> Packets(string path, string selection)
        {
            var result = await ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-select_streams", selection, "-show_streams",
                "-show_entries", "stream=index", "-of", "json", path]);
            check(result.ExitCode == 0, "Packet hash probe failed: " + result.Error);
            using var json = JsonDocument.Parse(result.Output);
            var hashes = new List<string>();
            // MOV may repacketize PCM and interleave tracks differently. Compare the
            // complete copied payload of each track, independent of packet boundaries.
            foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
            {
                var hash = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-i", path, "-map", "0:" + stream.GetProperty("index"), "-c", "copy", "-f", "hash", "-hash", "sha256", "-"]);
                check(hash.ExitCode == 0, "Payload hash failed: " + hash.Error); hashes.Add(hash.Output.Trim());
            }
            return string.Join("\n", hashes);
        }
        async Task CheckTracks(string output)
        {
            using var before = JsonDocument.Parse(infos[mov].RawJson); using var after = JsonDocument.Parse((await engine.Probe(output)).RawJson);
            var streams = after.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            check(streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") == 2 && streams.Any(s => s.GetProperty("codec_name").GetString() == "mov_text"), "MOV export lost independent audio or subtitle tracks.");
            check(await Packets(mov, "s") == await Packets(output, "s"), "MOV export changed subtitle packets.");
            check(after.RootElement.GetProperty("format").GetProperty("tags").GetProperty("title").GetString() == "Original title", "MOV export lost metadata.");
            var chapterProbe = await ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-show_chapters", "-of", "json", output]);
            using var chaptersJson = JsonDocument.Parse(chapterProbe.Output);
            check(chaptersJson.RootElement.GetProperty("chapters").EnumerateArray().Single().GetProperty("tags").GetProperty("title").GetString() == "Original chapter", "MOV export lost chapter metadata.");
        }
        async Task CheckVideoAttributes(string source, string output)
        {
            using var before = JsonDocument.Parse(infos[source].RawJson); using var after = JsonDocument.Parse((await engine.Probe(output)).RawJson);
            var a = before.RootElement.GetProperty("streams")[0]; var b = after.RootElement.GetProperty("streams")[0];
            foreach (var key in new[] { "pix_fmt", "avg_frame_rate", "color_range", "color_space", "color_transfer", "color_primaries" })
                if (a.TryGetProperty(key, out var value)) check(b.TryGetProperty(key, out var other) && value.ToString() == other.ToString(), "Original export changed " + key);
            if (source == prores || source == taggedProres) check(b.GetProperty("profile").GetString() == "HQ" && b.GetProperty("pix_fmt").GetString() == "yuv422p10le", "ProRes lost HQ profile / 10-bit 4:2:2.");
        }
        async Task ComparePixels(string source, string output, MediaInfo a, MediaInfo b, int angle)
        {
            var before = await Frame(source); var after = await Frame(output);
            check(before.Length == a.Width * a.Height * 3 && after.Length == b.Width * b.Height * 3, "Source-mode frame display dimensions are wrong.");
            foreach (var (fx, fy) in new[] { (.25, .25), (.75, .75) })
            {
                int x = (int)(b.Width * fx), y = (int)(b.Height * fy);
                var (sx, sy) = angle switch { 90 => (y, a.Height - 1 - x), 180 => (a.Width - 1 - x, a.Height - 1 - y), _ => (a.Width - 1 - y, x) };
                check(Enumerable.Range(0, 3).All(c => Math.Abs(before[(sy * a.Width + sx) * 3 + c] - after[(y * b.Width + x) * 3 + c]) < 30), "Source-mode displayed pixels rotated in wrong direction.");
            }
        }
        async Task<byte[]> Frame(string path)
        {
            var raw = Path.Combine(root, Guid.NewGuid() + ".rgb");
            var result = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-i", path, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", raw]);
            check(result.ExitCode == 0, "Source-mode frame decode failed: " + result.Error); return await File.ReadAllBytesAsync(raw);
        }
        void Reject(Action action, string message)
        { try { action(); } catch (ArgumentException) { check(true, message); return; } check(false, message); }
        ConversionOptions WithRotation() { var options = fastOptions.Clone(); options.Rotation = 90; return options; }
    }
}
