using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AvaMedia.Core;

internal static class OrientationChecks
{
    public static async Task<IReadOnlyList<string>> Run(MediaEngine engine, string root, string noFace, Action<bool, string> check)
    {
        OrientationFrameEvidence Evidence(int direction, double seconds = 0)
        {
            var scores = new double[4]; scores[direction / 90] = .95;
            return new(seconds, scores[0], scores[1], scores[2], scores[3]);
        }
        foreach (var direction in new[] { 0, 90, 180, 270 })
            check(VideoOrientationPolicy.Decide(Enumerable.Range(0, 8).Select(i => Evidence(direction, i)).ToArray()).Rotation == direction, "Temporal policy chose the wrong correction.");
        check(!VideoOrientationPolicy.Decide([Evidence(0), Evidence(0)]).IsCertain, "Two frames pass the evidence minimum.");
        check(!VideoOrientationPolicy.Decide([new(0, 0, 0, 0, 0), new(1, 0, 0, 0, 0), new(2, 0, 0, 0, 0)]).IsCertain, "No face silently becomes 0 degrees.");
        check(!VideoOrientationPolicy.Decide(Enumerable.Range(0, 8).Select(i => new OrientationFrameEvidence(i, .95, .94, 0, 0)).ToArray()).IsCertain, "Nearly tied directions pass as certain.");
        check(!VideoOrientationPolicy.Decide(Enumerable.Range(0, 8).Select(i => Evidence(i < 6 ? 0 : 180, i)).ToArray()).IsCertain, "Two contradictory frames are ignored.");
        check(VideoOrientationPolicy.Decide(Enumerable.Range(0, 8).Select(i => Evidence(i < 7 ? 0 : 180, i)).ToArray()).Rotation == 0, "One outlier overturns seven consistent frames.");

        var image = Path.Combine(AppContext.BaseDirectory, "Fixtures", "astronaut.png");
        check(File.Exists(image), "Public domain test portrait is missing.");
        var upright = Path.Combine(root, "人脸 upright O'Brien.mp4");
        var generated = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-loop", "1", "-i", image,
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1.2", "-r", "25", "-vf", "pad=640:512:64:0",
            "-c:v", "mpeg4", "-q:v", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", upright]);
        check(generated.ExitCode == 0, "Face fixture generation failed: " + generated.Error);
        var inputs = new List<BatchRotateInput>(); var detector = new VideoOrientationDetector(engine);
        var measurements = new List<object>(); var baseline = await Pixels(upright, engine);
        foreach (var baked in new[] { 0, 90, 180, 270 })
        {
            var path = upright;
            if (baked != 0)
            {
                path = Path.Combine(root, "人脸 baked-" + baked + ".mp4");
                var filter = baked switch { 90 => "transpose=1", 180 => "hflip,vflip", _ => "transpose=2" };
                var run = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-i", upright, "-vf", filter, "-c:v", "mpeg4", "-q:v", "2", "-c:a", "copy", path]);
                check(run.ExitCode == 0, "Baked rotation fixture failed: " + run.Error);
            }
            var info = await engine.Probe(path); var timer = Stopwatch.StartNew();
            var result = await detector.DetectAsync(path, info); timer.Stop();
            Console.WriteLine($"Orientation {baked}: {result.Rotation} / {result.Reason} / {timer.Elapsed.TotalSeconds:F2}s");
            if (!result.IsCertain) Console.WriteLine(JsonSerializer.Serialize(result.Evidence));
            check(result.Rotation == (360 - baked) % 360, "Real face orientation detection failed: " + baked + " / " + result.Reason);
            check(result.ValidFrames >= 3, "Real detection has insufficient valid frames.");
            inputs.Add(new(path, info, result.Rotation));
            measurements.Add(new { bakedRotation = baked, result, seconds = timer.Elapsed.TotalSeconds });
        }
        var hashes = inputs.ToDictionary(i => i.Path, i => SHA256.HashData(File.ReadAllBytes(i.Path)));
        var jobs = BatchRotate.CreateJobs(new(inputs, 0, "mp4", Path.Combine(root, "auto-upright")));
        check(jobs.Count == 3 && jobs.Select(j => j.Options.Rotation).SequenceEqual(new[] { 270, 180, 90 }), "Per-video rotations were lost or upright input was queued.");
        await new QueueService(engine).Run(jobs, 2);
        foreach (var job in jobs)
        {
            check(job.State == JobState.Completed, "Auto rotation output failed: " + job.Error);
            var info = await engine.Probe(job.Output);
            check(info.Width == 640 && info.Height == 512 && info.HasAudio, "Auto output lost geometry or audio.");
            using var json = JsonDocument.Parse(info.RawJson);
            foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
                if (stream.TryGetProperty("side_data_list", out var sides))
                    foreach (var side in sides.EnumerateArray())
                        if (side.TryGetProperty("rotation", out var angle)) check(Math.Abs(angle.GetDouble()) < .01, "Output retains a rotation matrix and could rotate twice.");
            var pixels = await Pixels(job.Output, engine);
            check(pixels.Length == baseline.Length, "Output decoded dimensions differ.");
            var error = pixels.Select((p, i) => Math.Abs(p - baseline[i])).Average();
            check(error < 12, "Auto correction pixels differ from upright baseline: " + error);
        }
        check(inputs.All(i => hashes[i.Path].SequenceEqual(SHA256.HashData(File.ReadAllBytes(i.Path)))), "Detection/rotation modified a source.");

        var tagged = Path.Combine(root, "人脸 phone-tag.mp4");
        var tag = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-display_rotation", "90", "-i", inputs[1].Path, "-c", "copy", tagged]);
        check(tag.ExitCode == 0, "Phone tag fixture failed.");
        check((await detector.DetectAsync(tagged, await engine.Probe(tagged))).Rotation == 0, "Already corrected phone metadata causes a second rotation.");
        var wrongTag = Path.Combine(root, "人脸 wrong-tag.mp4");
        check((await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-display_rotation", "90", "-i", upright, "-c", "copy", wrongTag])).ExitCode == 0, "Wrong tag fixture failed.");
        var wrongInfo = await engine.Probe(wrongTag); var wrongResult = await detector.DetectAsync(wrongTag, wrongInfo);
        check(wrongResult.Rotation == 90, "Incorrect metadata is blindly trusted.");
        var tagJobs = BatchRotate.CreateJobs(new([new(wrongTag, wrongInfo, wrongResult.Rotation)], 0, "mp4", Path.Combine(root, "auto-tag")));
        await new QueueService(engine).Run(tagJobs, 1);
        check(tagJobs[0].State == JobState.Completed && (await Pixels(tagJobs[0].Output, engine)).Select((p, i) => Math.Abs(p - baseline[i])).Average() < 12, "Metadata plus residual correction produces wrong pixels.");

        var unknown = await detector.DetectAsync(noFace, await engine.Probe(noFace));
        check(!unknown.IsCertain && unknown.Rotation is null, "Video without faces is assumed upright.");
        var mixed = Path.Combine(root, "人脸 mixed-direction.mp4");
        check((await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-i", upright, "-i", inputs[2].Path,
            "-filter_complex", "[0:v:0][1:v:0]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-c:v", "mpeg4", "-q:v", "2", mixed])).ExitCode == 0, "Mixed direction fixture failed.");
        check(!(await detector.DetectAsync(mixed, await engine.Probe(mixed))).IsCertain, "Video that changes direction gets a global rotation.");
        using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(50);
        var canceled = false; var elapsed = Stopwatch.StartNew();
        try { await detector.DetectAsync(upright, inputs[0].Info, ct: cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && elapsed.Elapsed < TimeSpan.FromSeconds(5), "Detection cancellation does not terminate promptly.");
        await File.WriteAllTextAsync(Path.Combine(root, "orientation-report.json"), JsonSerializer.Serialize(new { measurements, noFace = unknown, outputs = jobs.Select(j => j.Output).Append(tagJobs[0].Output) }, new JsonSerializerOptions { WriteIndented = true }));
        return jobs.Select(j => j.Output).Append(tagJobs[0].Output).ToArray();
    }

    private static async Task<byte[]> Pixels(string path, MediaEngine engine)
    {
        using var process = ProcessRunner.Start(engine.FFmpeg, ["-v", "error", "-i", path, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        var error = process.StandardError.ReadToEndAsync(); using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        return buffer.ToArray();
    }
}
