using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using Microsoft.Win32;

internal static class ProjectDetailChecks
{
    public static void Run()
    {
        var output = Path.GetFullPath("artifacts/player-details");
        Directory.CreateDirectory(output);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Localization.Apply("zh-CN");
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        bool HasClassic() => Application.Current!.Styles.OfType<StyleInclude>().Any(style => style.Source?.AbsolutePath.EndsWith("/Platinum.axaml") == true);
        Check(!HasClassic(), "Default Light startup does not parse Platinum styles");
        Skin.Apply("Dark"); Check(!HasClassic(), "Dark startup keeps classic styles deferred");
        Skin.Apply("MacOS9"); Check(HasClassic(), "Selecting Mac OS 9 loads its actual theme");
        var count = Application.Current!.Styles.Count;
        Skin.Apply("Light"); Skin.Apply("MacOS9"); Check(Application.Current.Styles.Count == count, "Theme switching reuses the classic style instance");
        Skin.Apply("Light");

        var source = Path.GetFullPath("artifacts/player-large-files/4k-large.avi");
        var job = new Job { Inputs = [source], Output = source, State = JobState.Completed };
        using var row = new JobRowDetails(job);
        var open = Stopwatch.StartNew(); row.Refresh(); var dispatchMs = open.Elapsed.TotalMilliseconds;
        Complete(row.MetadataReady);
        Check(new FileInfo(source).Length > uint.MaxValue && row.FileSummary.Contains("GB") && row.StateText.Contains("GB"), "Real video over 4 GiB has correct asynchronous source and output sizes");
        var metadata = row.MetadataReady;
        var bytesBefore = GC.GetTotalAllocatedBytes(); var progress = Stopwatch.StartNew();
        for (var index = 0; index < 10000; index++) { job.Progress = index % 100; row.RefreshProgress(); }
        var progressMs = progress.Elapsed.TotalMilliseconds;
        var progressAllocated = GC.GetTotalAllocatedBytes() - bytesBefore;
        Check(ReferenceEquals(row.MetadataReady, metadata), "Ten thousand progress changes do not restart metadata I/O");
        var missing = new Job { Inputs = [Path.Combine(output, "missing.mp4")] };
        using var stale = new JobRowDetails(missing); stale.Refresh();
        missing.Inputs = [source]; stale.Refresh(); Complete(stale.MetadataReady);
        Check(stale.FileSummary.Contains("GB"), "Late size reads cannot replace a newer source selection");
        using var abandoned = new JobRowDetails(job); abandoned.Refresh(); var pending = abandoned.MetadataReady; abandoned.Dispose(); Complete(pending);
        Check(pending.IsCompletedSuccessfully, "Closing a task row cancels metadata work without late UI updates");

        var storage = new Storage(Path.Combine(output, "state"));
        var batch = Enumerable.Range(0, 5000).Select(index => new Job { Inputs = [source], Output = Path.Combine(output, "output-" + index + ".mp4") }).ToArray();
        var write = Stopwatch.StartNew(); var save = storage.SaveJobsAsync(batch); var enqueueMs = write.Elapsed.TotalMilliseconds;
        Complete(save); var savedBytes = new FileInfo(Path.Combine(output, "state", "queue.json")).Length;
        Check(storage.LoadJobs().Count == batch.Length, "Five thousand jobs are persisted completely in the background");
        var older = storage.SaveJobsAsync(batch); var clear = storage.SaveJobsAsync([]); Complete(Task.WhenAll(older, clear));
        Check(storage.LoadJobs().Count == 0, "An older asynchronous queue snapshot cannot overwrite a clear action");
        var pendingSave = storage.SaveJobsAsync(batch); storage.SaveJobs([job]); Complete(pendingSave);
        Check(storage.LoadJobs().Count == 1, "A synchronous newer save supersedes pending progress persistence");

        var engine = new MediaEngine(new());
        var probe = engine.Probe(Path.GetFullPath("artifacts/player-large-files/many-chapters.mp4")); Complete(probe);
        using (var json = JsonDocument.Parse(probe.Result.RawJson))
            Check(probe.Result.RawJson.Length > 160000 && json.RootElement.GetProperty("chapters").GetArrayLength() == 2000,
                "Large valid FFprobe JSON preserves all two thousand chapters instead of truncating metadata");
        var updates = new List<double>();
        var hashTimer = Stopwatch.StartNew(); var hashing = FileHashing.Sha256Async([source], value => updates.Add(value)); Complete(hashing);
        var hashMs = hashTimer.Elapsed.TotalMilliseconds;
        string expected;
        using (var stream = File.OpenRead(source)) expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        Check(hashing.Result[0] == expected + "  " + Path.GetFileName(source) && updates.Count > 2 && updates[^1] == 100,
            "Streaming checksum matches SHA256 for the real file over 4 GiB and reports intermediate progress");
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = FileHashing.Sha256Async([source], value => { if (value > 0) cancel.Cancel(); }, cancel.Token);
            while (!cancelled.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
            Check(cancelled.IsCanceled, "Large-file checksum cancels during streaming and releases its buffer");
        }

        if (OperatingSystem.IsWindows())
        {
            var keyName = @"Software\AvaMedia.Tests\Player-" + Guid.NewGuid().ToString("N");
            using var root = Registry.CurrentUser.CreateSubKey(keyName);
            try
            {
                using (var other = root.CreateSubKey(@"Software\Classes\.mp4\OpenWithProgids")) other.SetValue("Other.Player", "");
                using (var other = root.CreateSubKey(@"Software\Classes\.mp4")) other.SetValue("", "Other.Video");
                var executable = Path.GetFullPath("src/AvaMedia.Desktop/bin/Release/net8.0/AvaMedia.Desktop.exe");
                SystemPlayerIntegration.RegisterWindows(executable, root);
                using (var command = root.OpenSubKey(@"Software\Classes\AvaMedia.Player.Media\shell\open\command"))
                    Check(command?.GetValue("")?.ToString() == SystemPlayerIntegration.OpenCommand(executable), "Desktop file activation quotes the executable and opens the real --play path");
                using (var types = root.OpenSubKey(@"Software\AvaMedia\Player\Capabilities\FileAssociations"))
                {
                    var allTypes = true;
                    foreach (var extension in SystemPlayerIntegration.Extensions) allTypes &= types?.GetValue(extension)?.ToString() == "AvaMedia.Player.Media";
                    Check(allTypes, "Every declared video and audio extension is registered");
                }
                using (var original = root.OpenSubKey(@"Software\Classes\.mp4"))
                    Check(original?.GetValue("")?.ToString() == "Other.Video", "Desktop registration preserves the current default association");
                SystemPlayerIntegration.UnregisterWindows(executable, root);
                using (var other = root.OpenSubKey(@"Software\Classes\.mp4\OpenWithProgids"))
                    Check(other?.GetValue("Other.Player") is not null && other.GetValue("AvaMedia.Player.Media") is null, "Unregister removes only AvaMedia's Open with entry");
            }
            finally { root.Close(); Registry.CurrentUser.DeleteSubKeyTree(keyName); }
        }
        var report = new { checks, sizeReadEnqueueMs = dispatchMs, progressMs, progressAllocatedBytes = progressAllocated,
            queueSaveEnqueueMs = enqueueMs, savedQueueBytes = savedBytes, metadataJsonChars = probe.Result.RawJson.Length, largeFileHashMs = hashMs };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Complete(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed.TotalSeconds < 30) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException();
        task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
    }
}
