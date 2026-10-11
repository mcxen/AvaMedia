using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AvaMedia.Core;

/// <summary>A model worker cannot outlive its application, including when managed exit handlers never run.</summary>
internal sealed class OwnedModelProcess : IAsyncDisposable
{
    private static readonly object LifetimeGate = new();
    private static readonly HashSet<OwnedModelProcess> Live = [];
    private static readonly CancellationTokenSource LifetimeCancellation = new();
    private static bool _shuttingDown;
    private readonly SafeFileHandle? _job;
    private int _stopped, _disposed;
    internal Process Process { get; }
    internal bool IsAlive => Volatile.Read(ref _stopped) == 0 && !Process.HasExited;
    internal static CancellationToken Lifetime => LifetimeCancellation.Token;

    // The application owns the only write end of stdin. EOF also arrives on SIGKILL/crash.
    // The server gets /dev/null instead, so it cannot consume the lifetime pipe. The shell
    // reaps both children, forwards the server's exit code, and never polls or sleeps.
    private const string UnixGuardian = """
        IFS= read -r permit || exit 0
        [ "$permit" = start ] || exit 1
        exec 3<&0
        "$@" </dev/null 3<&- &
        server=$!
        guardian=$$
        trap 'kill -KILL "$server" 2>/dev/null || :; wait "$server" 2>/dev/null || :' HUP INT TERM
        (
            while IFS= read -r ignored; do :; done
            kill -TERM "$guardian" 2>/dev/null || :
        ) <&3 3<&- &
        watcher=$!
        exec 3<&-
        wait "$server"
        result=$?
        trap '' HUP INT TERM
        kill -KILL "$watcher" 2>/dev/null || :
        wait "$watcher" 2>/dev/null || :
        exit "$result"
        """;

    static OwnedModelProcess() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    private OwnedModelProcess(Process process, SafeFileHandle? job) { Process = process; _job = job; }

    internal static async Task<OwnedModelProcess> StartAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        var snapshot = arguments.ToArray();
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => Start(executable, snapshot, ct), ct).ConfigureAwait(false);
    }

    private static OwnedModelProcess Start(string executable, string[] arguments, CancellationToken ct)
    {
        // Register before permitting any model load; shutdown cannot miss a starting worker.
        lock (LifetimeGate)
        {
            ct.ThrowIfCancellationRequested();
            if (_shuttingDown) throw new OperationCanceledException(Lifetime);
            SafeFileHandle? job = null;
            Process? process = null;
            OwnedModelProcess? owner = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    job = CreateWindowsJob();
                    process = ProcessRunner.Start(executable, arguments);
                    if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    process = ProcessRunner.Start("/bin/sh", ["-c", UnixGuardian, "avamedia-model-guardian", executable, .. arguments], input: true);
                    CpuResourcePolicy.LowerWorkerPriority(process);
                }
                owner = new(process, job);
                Live.Add(owner);
                ct.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsWindows())
                {
                    process.StandardInput.WriteLine("start");
                    process.StandardInput.Flush();
                }
                return owner;
            }
            catch
            {
                if (owner is not null) Live.Remove(owner);
                job?.Dispose();
                if (process is not null)
                {
                    try
                    {
                        if (OperatingSystem.IsWindows()) { if (!process.HasExited) process.Kill(true); }
                        else process.StandardInput.Close();
                        if (!process.WaitForExit(5000) && !process.HasExited) process.Kill(true);
                    }
                    catch (Exception error) when (IsProcessError(error)) { Trace.TraceError("Model startup cleanup: {0}", error); }
                    finally { process.Dispose(); }
                }
                throw;
            }
        }
    }

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            if (OperatingSystem.IsWindows()) _job?.Dispose();
            else Process.StandardInput.Close();
        }
        catch (Exception error) when (IsProcessError(error))
        { Trace.TraceError("Model lifetime pipe cleanup: {0}", error); Kill(); }
    }

    private void Kill()
    {
        try { if (!Process.HasExited) Process.Kill(true); }
        catch (Exception error) when (IsProcessError(error)) { Trace.TraceError("Model worker cleanup: {0}", error); }
    }

    internal static void Shutdown()
    {
        OwnedModelProcess[] workers;
        lock (LifetimeGate)
        {
            _shuttingDown = true;
            workers = [.. Live];
        }
        // Signal every worker first, so shutdown time does not grow with the model count.
        foreach (var worker in workers) worker.Stop();
        try { LifetimeCancellation.Cancel(); }
        catch (AggregateException error) { Trace.TraceError("Model shutdown cancellation: {0}", error); }
        var started = Stopwatch.GetTimestamp();
        foreach (var worker in workers)
        {
            try
            {
                var remaining = Math.Max(0, 5000 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (!worker.Process.WaitForExit(remaining)) worker.Kill();
            }
            catch (Exception error) when (IsProcessError(error)) { Trace.TraceError("Model shutdown wait: {0}", error); }
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(Task.CompletedTask);

    internal async ValueTask DisposeAsync(Task drain)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        try
        {
            try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Kill();
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            await drain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            _job?.Dispose();
            lock (LifetimeGate) Live.Remove(this);
            Process.Dispose();
        }
    }

    private static bool IsProcessError(Exception error) => error is Win32Exception or InvalidOperationException or IOException or NotSupportedException;

    private static SafeFileHandle CreateWindowsJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            var creationError = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw creationError;
        }
        var limits = new JobLimits { Basic = new() { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) return job;
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        job.Dispose();
        throw error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits information, uint length);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
