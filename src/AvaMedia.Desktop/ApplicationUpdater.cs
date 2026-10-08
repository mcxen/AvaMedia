using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal enum UpdatePhase { Idle, Downloading, Preparing, Ready, Failed }
internal sealed record UpdateProgress(UpdatePhase Phase, long ReceivedBytes = 0, long TotalBytes = 0, string? Error = null)
{
    public bool IsBusy => Phase is UpdatePhase.Downloading or UpdatePhase.Preparing;
}

internal sealed class ApplicationUpdater
{
    public static ApplicationUpdater Shared { get; } = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _automaticDownload;
    private CancellationTokenSource? _activeDownload;
    private volatile PreparedUpdate? _pending;
    private volatile bool _exiting;
    private volatile UpdateProgress _progress = new(UpdatePhase.Idle);
    public UpdateProgress Progress => _progress;
    public bool IsDownloading => _activeDownload is not null || _automaticDownload is not null || Progress.IsBusy;
    public bool CanCancelDownload => _activeDownload is { IsCancellationRequested: false };
    public event EventHandler? ProgressChanged;
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "Updates");
    private static string ErrorPath => Path.Combine(Root, "install-error.txt");
    private sealed record PreparedUpdate(string Package, string Target, string Stage, string Backup, string Kind);
    public bool IsPrepared => _pending is not null;

    public static bool CanInstall => TryTarget(out _, out _);
    private static bool TryTarget(out string target, out string kind)
    {
        target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory); kind = "";
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            string.Equals(Environment.ProcessPath, Path.Combine(target, "AvaMedia.Desktop.exe"), StringComparison.OrdinalIgnoreCase))
        { kind = File.Exists(Path.Combine(target, "unins000.exe")) ? "installer" : "portable"; return true; }
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            var macos = new DirectoryInfo(target);
            if (macos.Name == "MacOS" && macos.Parent?.Name == "Contents" && macos.Parent.Parent is { } bundle && bundle.Extension == ".app")
            { target = bundle.FullName; kind = "mac"; return true; }
        }
        return false;
    }

    public void PreferencesChanged(AppSettings settings)
    {
        if (!settings.AutoUpdate)
        {
            _automaticDownload?.Cancel();
            // A manually prepared update still represents the user's explicit update request.
            lock (_stateGate) { if (_preparedAutomatically) DiscardPrepared(); }
        }
    }
    private volatile bool _preparedAutomatically;
    public async Task StartupAsync(Window owner, AppSettings settings, Func<CancellationToken, Task<UpdateResult>> check, CancellationToken ct)
    {
        bool Silent() => settings.AutoUpdate && settings.SilentUpdate;
        try
        {
            if (FirstRunSetup.Failed) return;
            if (File.Exists(ErrorPath) && !Silent())
            {
                var error = await File.ReadAllTextAsync(ErrorPath, ct);
                File.Delete(ErrorPath);
                Notifications.UpdateNotifications.Error(owner, "更新安装失败", error);
            }
            if (!settings.CheckForUpdates) return;
            var result = await check(ct);
            if (!settings.CheckForUpdates || !result.HasUpdate || !result.CheckSucceeded || _exiting) return;
            if (settings.AutoUpdate && result.Asset is not null && CanInstall)
            {
                using var download = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                _automaticDownload = download;
                try
                {
                    Notifications.UpdateNotifications.Show(owner, result, settings, automatic: true);
                    await PrepareAsync(result, automatic: true, download.Token);
                }
                finally { _automaticDownload = null; }
            }
            else Notifications.UpdateNotifications.Show(owner, result, settings, automatic: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.TraceWarning("自动更新失败：{0}", ex.Message);
            if (!Silent() && !_exiting && Progress.Phase != UpdatePhase.Failed)
                Notifications.UpdateNotifications.Error(owner, "更新失败", ex.Message);
        }
    }

    public Task PrepareAsync(UpdateResult result, bool automatic, CancellationToken ct) =>
        Task.Run(() => PrepareCoreAsync(result, automatic, ct), ct);

    public void CancelDownload()
    {
        try { _activeDownload?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ReportProgress(UpdateProgress progress)
    {
        _progress = progress;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PrepareCoreAsync(UpdateResult result, bool automatic, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        await _gate.WaitAsync(ct);
        _activeDownload = linked;
        string? work = null, stage = null;
        try
        {
            lock (_stateGate)
            {
                if (_pending is not null) { if (!automatic) _preparedAutomatically = false; return; }
            }
            if (result.Asset is not { } asset || !TryTarget(out var target, out var kind))
                throw new InvalidOperationException("当前安装不支持应用内更新，请从发布页下载安装包。");
            ReportProgress(new(UpdatePhase.Downloading, TotalBytes: asset.Size));
            var id = Guid.NewGuid().ToString("N");
            var parent = Path.GetDirectoryName(target)!;
            stage = Path.Combine(parent, ".avamedia-update-" + id + (kind == "mac" ? ".app" : ""));
            // Verify that this installation can be replaced without requesting elevation.
            Directory.CreateDirectory(stage);
            work = Path.Combine(Root, id); Directory.CreateDirectory(work);
            var package = Path.Combine(work, asset.Name);
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("AvaMedia/" + AppIdentity.Version);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("安装包大小不匹配。");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long received = 0;
                var lastReport = Environment.TickCount64;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("安装包大小不匹配。");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    var now = Environment.TickCount64;
                    if (now - lastReport >= 100 || received == asset.Size)
                    {
                        ReportProgress(new(UpdatePhase.Downloading, received, asset.Size));
                        lastReport = now;
                    }
                }
                if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包 SHA256 校验失败。");
            }
            ReportProgress(new(UpdatePhase.Preparing, asset.Size, asset.Size));
            if (kind == "portable")
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(package, stage), ct);
                if (!File.Exists(Path.Combine(stage, "AvaMedia.Desktop.exe"))) throw new InvalidDataException("安装包缺少应用程序。");
            }
            else if (kind == "mac") await PrepareMacAsync(package, stage, result.LatestVersion!, ct);
            lock (_stateGate)
            {
                ct.ThrowIfCancellationRequested();
                _preparedAutomatically = automatic;
                _pending = new(package, target, stage, target + ".update-backup-" + id, kind);
                work = stage = null;
                ReportProgress(new(UpdatePhase.Ready, asset.Size, asset.Size));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReportProgress(new(UpdatePhase.Idle));
            throw;
        }
        catch (Exception ex)
        {
            var error = ex is UnauthorizedAccessException
                ? new IOException("应用目录无法写入，请从发布页手动安装更新。", ex) : ex;
            ReportProgress(Progress with { Phase = UpdatePhase.Failed, Error = error.Message });
            if (error != ex) throw error;
            throw;
        }
        finally
        {
            if (stage is not null) DeleteDirectory(stage);
            if (work is not null) DeleteDirectory(work);
            _activeDownload = null;
            _gate.Release();
        }
    }
    private static async Task PrepareMacAsync(string package, string stage, Version version, CancellationToken ct)
    {
        var mount = Path.Combine(Path.GetDirectoryName(package)!, "volume");
        Directory.CreateDirectory(mount);
        try
        {
            await RunAsync("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-mountpoint", mount, package], ct);
            var bundles = Directory.GetDirectories(mount, "*.app");
            if (bundles.Length != 1) throw new InvalidDataException("安装包中的应用无效。");
            await RunAsync("/usr/bin/ditto", [bundles[0], stage], ct);
        }
        finally
        {
            var detach = await ProcessRunner.Run("/usr/bin/hdiutil", ["detach", mount], CancellationToken.None);
            if (detach.ExitCode != 0) Trace.TraceWarning("更新磁盘映像未能卸载：{0}", detach.Error);
        }
        await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", stage], ct);
        var info = Path.Combine(stage, "Contents", "Info.plist");
        var actual = await ProcessRunner.Run("/usr/libexec/PlistBuddy", ["-c", "Print :CFBundleShortVersionString", info], ct);
        if (actual.ExitCode != 0 || actual.Output.Trim() != version.ToString(3) ||
            !File.Exists(Path.Combine(stage, "Contents", "MacOS", "AvaMedia.Desktop")))
            throw new InvalidDataException("安装包版本或应用程序无效。");
    }
    private static async Task RunAsync(string executable, string[] args, CancellationToken ct)
    {
        var result = await ProcessRunner.Run(executable, args, ct);
        if (result.ExitCode != 0) throw new IOException("准备更新失败：" + result.Error);
    }
    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) { Trace.TraceWarning("更新临时目录：{0}", ex.Message); }
    }
    private void DiscardPrepared()
    {
        if (_pending is not { } update) return;
        _pending = null; DeleteDirectory(update.Stage); DeleteDirectory(Path.GetDirectoryName(update.Package)!);
        ReportProgress(new(UpdatePhase.Idle));
    }

    public void InstallOnExit()
    {
        PreparedUpdate? update;
        lock (_stateGate) { _exiting = true; _lifetime.Cancel(); update = _pending; }
        if (update is null) return;
        try
        {
            var windows = OperatingSystem.IsWindows();
            var resource = "AvaMedia.Desktop.Updates." + (windows ? "Install.ps1" : "Install.sh");
            var helper = Path.Combine(Path.GetDirectoryName(update.Package)!, windows ? "install.ps1" : "install.sh");
            using (var input = typeof(ApplicationUpdater).Assembly.GetManifestResourceStream(resource)!)
            using (var reader = new StreamReader(input))
                File.WriteAllText(helper, reader.ReadToEnd(), new System.Text.UTF8Encoding(windows));
            var start = new ProcessStartInfo(windows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
            if (windows)
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(arg);
            foreach (var arg in new[] { helper, Environment.ProcessId.ToString(), update.Kind, update.Package, update.Target, update.Stage, update.Backup, ErrorPath })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("无法启动更新安装程序。");
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(Root); File.WriteAllText(ErrorPath, ex.Message); }
            catch (Exception logError) { Trace.TraceWarning("更新安装未能启动：{0}; {1}", ex.Message, logError.Message); }
        }
    }
}
