using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace AvaMedia.Core.Compute;

public interface IComputeClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemComputeClock : IComputeClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public interface IComputeFiles
{
    string? Read(string path);
    void Write(string path, string content);
    DateTimeOffset LastWrite(string path);
}
public sealed class ComputeFiles : IComputeFiles
{
    public string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    public DateTimeOffset LastWrite(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTimeOffset.MinValue;
    public void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public interface IComputeProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
    Task<ProcessResult> RunToStreamAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken ct);
}
public sealed class ComputeProcessRunner : IComputeProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        try { return await ProcessRunner.Run(executable, arguments, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("探测超时（" + timeout.TotalSeconds + " 秒）。"); }
    }
    public async Task<ProcessResult> RunToStreamAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken ct)
    {
        using var process = await ProcessRunner.StartAsync(executable, arguments, ct).ConfigureAwait(false);
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        using var cancellation = ct.Register(Kill);
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            return new(process.ExitCode, "", await error.ConfigureAwait(false));
        }
        finally { Kill(); await process.WaitForExitAsync().ConfigureAwait(false); await error.ConfigureAwait(false); }
    }
}
public static class ComputeEnvironment
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia");
    public static ComputePlatform Platform => OperatingSystem.IsWindows() ? ComputePlatform.Windows : OperatingSystem.IsMacOS() ? ComputePlatform.MacOS : ComputePlatform.Linux;
    public static IComputeProcessRunner Processes { get; set; } = new ComputeProcessRunner();
    public static ComputeCapabilities Capabilities { get; set; } = new();
    // Includes driver identity without invoking a vendor utility. Callers/tests may supply a richer driver digest.
    public static string Fingerprint(string executable = "", IComputeFiles? files = null, string? driverDigest = null)
    {
        files ??= new ComputeFiles();
        driverDigest ??= DriverDigest();
        var text = string.Join('|', RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture,
            Environment.MachineName, executable, files.LastWrite(executable).UtcTicks, typeof(InferenceSession).Assembly.GetName().Version, driverDigest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    private static string DriverDigest()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return string.Join('|', new[] { "/proc/driver/nvidia/version", "/sys/module/amdgpu/version", "/sys/module/i915/srcversion", "/sys/module/xe/srcversion" }
                    .Where(File.Exists).Select(File.ReadAllText).Concat(Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri").Order() : []));
            if (OperatingSystem.IsWindows())
                return string.Join('|', new[] { "nvldumdx.dll", "nvwgf2umx.dll", "atidxx64.dll", "igd12umd64.dll", "d3d12.dll" }
                    .Select(name => Path.Combine(Environment.SystemDirectory, name)).Where(File.Exists)
                    .Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks));
            return RuntimeInformation.OSDescription;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return RuntimeInformation.OSDescription; }
    }
}
