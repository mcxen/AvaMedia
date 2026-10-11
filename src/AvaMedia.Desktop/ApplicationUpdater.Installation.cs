using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Desktop;

internal sealed partial class ApplicationUpdater
{
    private sealed record InstallerIdentity(int Pid, long StartedUtcTicks);
    private static string Work(PreparedUpdate update) => Path.GetDirectoryName(update.Package)!;
    private static string Marker(PreparedUpdate update, string name) => Path.Combine(Work(update), name);
    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    private static void SavePrepared(PreparedUpdate update) => WriteJson(Marker(update, "pending.json"), update);
    private static Process? FindInstaller(PreparedUpdate update)
    {
        var ready = ReadReadyInstaller(update);
        if (ready is not null) return ready;
        Process? process = null;
        try
        {
            if (!File.Exists(Marker(update, "helper.json"))) return null;
            var identity = JsonSerializer.Deserialize<InstallerIdentity>(File.ReadAllText(Marker(update, "helper.json")));
            if (identity is null) return null;
            process = Process.GetProcessById(identity.Pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks) return process;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.TraceInformation("Update helper identity: {0}", error.Message); }
        process?.Dispose();
        return null;
    }

    private static Process? ReadReadyInstaller(PreparedUpdate update)
    {
        Process? process = null;
        try
        {
            var path = Marker(update, "ready");
            if (!File.Exists(path) || !int.TryParse(File.ReadAllText(path), out var pid)) return null;
            process = Process.GetProcessById(pid);
            var created = process.StartTime.ToUniversalTime();
            var acknowledged = File.GetLastWriteTimeUtc(path);
            if (!process.HasExited && created <= acknowledged && acknowledged - created < TimeSpan.FromMinutes(1)) return process;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.TraceInformation("Update helper acknowledgement: {0}", error.Message); }
        process?.Dispose();
        return null;
    }

    private static async Task EnsureInstallerAsync(PreparedUpdate update, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(Marker(update, "cancel"))) throw new OperationCanceledException(ct);
        using var existing = FindInstaller(update);
        using var acknowledged = ReadReadyInstaller(update);
        if (acknowledged is not null) return;
        var windows = OperatingSystem.IsWindows();
        var helper = Marker(update, windows ? "install.ps1" : "install.sh");
        if (existing is null)
        {
            using var input = typeof(ApplicationUpdater).Assembly.GetManifestResourceStream("AvaMedia.Desktop.Updates." + (windows ? "Install.ps1" : "Install.sh"))
                ?? throw new IOException("缺少更新安装程序。");
            using var reader = new StreamReader(input);
            File.WriteAllText(helper, await reader.ReadToEndAsync(ct), new UTF8Encoding(windows));
        }
        var start = new ProcessStartInfo(windows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/usr/bin/nohup") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, WorkingDirectory = Work(update) };
        if (windows)
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(arg);
        else start.ArgumentList.Add("/bin/sh");
        var targetKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(windows ? update.Target.ToUpperInvariant() : update.Target)));
        foreach (var arg in new[] { helper, update.Kind, update.Package, update.Target, update.Stage, update.Backup, ErrorPath,
            update.Version, update.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), update.Sha256.ToLowerInvariant(), Path.Combine(Root, "install-" + targetKey + ".lock") })
            start.ArgumentList.Add(arg);
        using var launched = existing is null ? Process.Start(start) ?? throw new IOException("无法启动更新安装守护进程。") : null;
        if (launched is not null)
        {
            // Save the starting process too, so cancellation can wait for it before removing files.
            WriteJson(Marker(update, "helper.json"), new InstallerIdentity(launched.Id, launched.StartTime.ToUniversalTime().Ticks));
            launched.StandardInput.Close();
        }
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            ct.ThrowIfCancellationRequested();
            using var ready = ReadReadyInstaller(update);
            if (ready is not null)
            {
                WriteJson(Marker(update, "helper.json"), new InstallerIdentity(ready.Id, ready.StartTime.ToUniversalTime().Ticks));
                return;
            }
            if (launched is { HasExited: true } && launched.ExitCode != 0 || existing is { HasExited: true }) break;
            await Task.Delay(50, ct);
        }
        throw new IOException("更新安装守护进程未能就绪，请查看 " + Marker(update, "install.log"));
    }

    internal async Task RestorePreparedAsync(AvaMedia.Core.AppSettings settings, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_pending is not null || !TryTarget(out var target, out var kind) || !Directory.Exists(Root)) return;
            var updates = new List<PreparedUpdate>();
            foreach (var work in Directory.EnumerateDirectories(Root))
            {
                var manifest = Path.Combine(work, "pending.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    var update = JsonSerializer.Deserialize<PreparedUpdate>(await File.ReadAllTextAsync(manifest, ct));
                    if (update is not null && ValidPrepared(update, work, target, kind)) updates.Add(update);
                }
                catch (Exception error) when (error is IOException or JsonException or ArgumentException)
                { AppDiagnostics.Record("Read prepared update", error); }
            }
            foreach (var update in updates.OrderByDescending(update => System.Version.Parse(update.Version)))
            {
                if (File.Exists(Marker(update, "cancel")) || System.Version.Parse(update.Version) <= System.Version.Parse(AvaMedia.Core.AppIdentity.Version)
                    || update.Automatic && !settings.AutoUpdate)
                {
                    File.WriteAllText(Marker(update, "cancel"), "");
                    _ = CleanupCancelledAsync(update);
                    continue;
                }
                if (!File.Exists(update.Package) || !Directory.Exists(update.Stage)) throw new IOException("已下载的更新文件缺失，请重新下载更新。");
                await EnsureInstallerAsync(update, ct);
                lock (_stateGate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (update.Automatic && !(_startupSettings ?? settings).AutoUpdate)
                    {
                        File.WriteAllText(Marker(update, "cancel"), "");
                        _ = CleanupCancelledAsync(update);
                        return;
                    }
                    File.WriteAllText(Marker(update, "armed"), "");
                    _pending = update; _preparedAutomatically = update.Automatic;
                }
                ReportProgress(new(UpdatePhase.Ready, update.Size, update.Size));
                MonitorInstaller(update);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        { AppDiagnostics.Record("Restore prepared update", error); ReportProgress(new(UpdatePhase.Failed, Error: error.Message)); }
        finally { _gate.Release(); }
    }

    private static bool ValidPrepared(PreparedUpdate update, string work, string target, string kind)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var id = Path.GetFileName(work);
        if (!Guid.TryParseExact(id, "N", out _) || !string.Equals(update.Target, target, comparison) || update.Kind != kind
            || !System.Version.TryParse(update.Version, out var version) || version.Build < 0 || update.Size <= 0
            || update.Sha256 is not { Length: 64 } || !update.Sha256.All(Uri.IsHexDigit)) return false;
        var suffix = kind == "mac" ? "osx-arm64.dmg" : kind == "installer" ? "win-x64-setup.exe" : "win-x64-portable.zip";
        return string.Equals(update.Package, Path.Combine(work, $"AvaMedia-{version.ToString(3)}-{suffix}"), comparison)
            && string.Equals(update.Stage, Path.Combine(Path.GetDirectoryName(target)!, ".avamedia-update-" + id + (kind == "mac" ? ".app" : "")), comparison)
            && string.Equals(update.Backup, target + ".update-backup-" + id, comparison);
    }

    private void MonitorInstaller(PreparedUpdate update) => _ = Task.Run(async () =>
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token);
                if (_pending?.Package != update.Package) return;
                using var guard = ReadReadyInstaller(update);
                try
                {
                    if (guard is null) await EnsureInstallerAsync(update, _lifetime.Token);
                    ArmInstaller(update);
                }
                catch (IOException error)
                {
                    if (_pending?.Package != update.Package) return;
                    AppDiagnostics.Record("Restart update installer", error);
                    ReportProgress(new(UpdatePhase.Failed, Error: error.Message));
                    await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppDiagnostics.Record("Monitor update installer", error); }
    });

    private void ArmInstaller(PreparedUpdate update)
    {
        lock (_stateGate)
        {
            if (_exiting || _pending?.Package != update.Package) return;
            if (!File.Exists(Marker(update, "armed"))) File.WriteAllText(Marker(update, "armed"), "");
            if (Progress.Phase != UpdatePhase.Ready) ReportProgress(new(UpdatePhase.Ready, update.Size, update.Size));
        }
    }

    internal async Task RetryInstallerAsync()
    {
        if (_pending is not { } update) return;
        try
        {
            await EnsureInstallerAsync(update, _lifetime.Token);
            ArmInstaller(update);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppDiagnostics.Record("Retry update installer", error);
            if (_pending?.Package == update.Package) ReportProgress(new(UpdatePhase.Failed, Error: error.Message));
        }
    }

    private static async Task CleanupCancelledAsync(PreparedUpdate update)
    {
        try
        {
            using var guard = FindInstaller(update);
            if (guard is not null)
            {
                try { await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { return; } // Retain files until the helper acknowledges cancellation.
            }
            DeleteDirectory(update.Stage); DeleteDirectory(Work(update));
        }
        catch (Exception error) { AppDiagnostics.Record("Cancel prepared update", error); }
    }
}
