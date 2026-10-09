using System.Text;

namespace AvaMedia.Core;

/// <summary>Decode every possible bisection query once, in one short source read per boundary.</summary>
internal sealed record PersonBoundaryFrames(double Start, double Step, byte[][] Frames)
{
    public byte[] At(double seconds) => Frames[checked((int)Math.Round((seconds - Start) / Step) - 1)];

    public static async Task<PersonBoundaryFrames> ReadAsync(IMediaEngine engine, string path, int stream,
        double start, double end, int size, CancellationToken ct)
    {
        var width = end - start;
        var divisions = 1;
        while (width / divisions > .1) divisions *= 2;
        var count = divisions - 1;
        if (count is < 1 or > 63) throw new ArgumentException("人物边界解码区间超出范围。");
        var step = width / divisions;
        var filter = new StringBuilder($"[0:v:{stream}]split={count}");
        for (var index = 0; index < count; index++) filter.Append($"[s{index}]");
        filter.Append(';');
        for (var index = 0; index < count; index++)
            filter.Append($"[s{index}]trim=start={MediaEngine.Number((index + 1) * step)},select=eq(n\\,0),setpts=PTS-STARTPTS,{PersonClipFrameSource.Filter(size)}[f{index}];");
        for (var index = 0; index < count; index++) filter.Append($"[f{index}]");
        filter.Append($"concat=n={count}:v=1:a=0,setpts=N/(30*TB)[frames]");
        List<string> args = ["-v", "error", "-nostdin", "-filter_complex_threads", "1", "-threads",
            (engine.Settings.MultiThread ? Math.Clamp(engine.Settings.CpuThreads, 1, 16) : 1).ToString(),
            "-ss", MediaEngine.Number(start), "-t", MediaEngine.Number(width + .2), "-i", path,
            "-filter_complex", filter.ToString(), "-map", "[frames]", "-an", "-sn", "-frames:v", count.ToString(),
            "-fps_mode", "passthrough", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"];
        using var process = await ProcessRunner.StartAsync(engine.FFmpeg, args, ct).ConfigureAwait(false);
        using var cancellation = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync();
        var frames = new byte[count][];
        try
        {
            for (var index = 0; index < count; index++)
            {
                frames[index] = new byte[checked(size * size * 3)];
                if (!await PersonClipFrameSource.ReadFrameAsync(process.StandardOutput.BaseStream, frames[index], ct).ConfigureAwait(false))
                    throw new InvalidDataException("人物边界画面不完整：" + await error.ConfigureAwait(false));
            }
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("人物边界解码失败：" + await error.ConfigureAwait(false));
            return new(start, step, frames);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await error.ConfigureAwait(false);
        }
    }
}
