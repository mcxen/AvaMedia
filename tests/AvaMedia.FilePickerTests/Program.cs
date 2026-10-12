using System.Collections.Concurrent;
using AvaMedia.Core;
using AvaMedia.FileUi;

var root = Path.Combine(Path.GetTempPath(), "avamedia-picker-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
async Task<T> Await<T>(TaskCompletionSource<T> signal) => await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
try
{
    var path = Path.Combine(root, "sample.mp4");
    await File.WriteAllTextAsync(path, "test input identity");
    var stamp = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    var calls = 0;
    Task<FilePickerMedia> Read(string _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref calls); return Task.FromResult(new FilePickerMedia(201, 1920, 1080, Thumbnail: [1, 2, 3])); }
    var cacheRoot = Path.Combine(root, "cache");
    var cache = new FilePickerMediaCache(Read, cacheRoot);
    var first = await cache.GetAsync(path, 19, stamp, default);
    var again = await cache.GetAsync(path, 19, stamp, default);
    Check(calls == 1 && again.Duration == first.Duration && again.Thumbnail!.SequenceEqual(first.Thumbnail!), "Memory cache reuses metadata and cover");
    var restored = await new FilePickerMediaCache(Read, cacheRoot).GetAsync(path, 19, stamp, default);
    Check(calls == 1 && restored.Width == 1920, "A new cache instance restores disk data without probing");
    await cache.GetAsync(path, 20, stamp, default);
    await cache.GetAsync(path, 20, stamp.AddSeconds(1), default);
    var secondPath = Path.Combine(root, "second.mp4"); await File.WriteAllTextAsync(secondPath, "other");
    await cache.GetAsync(secondPath, 20, stamp.AddSeconds(1), default);
    Check(calls == 4, "Path, size and modification time independently invalidate the identity");
    var file = Directory.GetFiles(cacheRoot, "*.cache").First(); await File.WriteAllTextAsync(file, "broken");
    var before = calls;
    var recovery = new FilePickerMediaCache(Read, cacheRoot);
    foreach (var identity in new[] { (path, 19UL, stamp), (path, 20UL, stamp), (path, 20UL, stamp.AddSeconds(1)), (secondPath, 20UL, stamp.AddSeconds(1)) })
        await recovery.GetAsync(identity.Item1, identity.Item2, identity.Item3, default);
    Check(calls == before + 1, "Corrupt disk entry is regenerated while valid entries are reused");

    var started = Signal<bool>(); var release = Signal<bool>(); var reads = 0;
    var dedup = new FilePickerMediaCache(async (_, token) => { Interlocked.Increment(ref reads); started.TrySetResult(true); await release.Task.WaitAsync(token); return new(12); }, Path.Combine(root, "dedup"));
    var one = dedup.GetAsync(path, 19, stamp, default); await Await(started);
    var two = dedup.GetAsync(path, 19, stamp, default); release.SetResult(true);
    await Task.WhenAll(one, two);
    Check(reads == 1, "Concurrent requests for one identity share one probe");

    started = Signal<bool>(); release = Signal<bool>(); reads = 0;
    var cancelledCache = new FilePickerMediaCache(async (_, _) => { Interlocked.Increment(ref reads); started.TrySetResult(true); await release.Task; return new(42); }, Path.Combine(root, "cancelled"));
    using (var cancellation = new CancellationTokenSource())
    {
        var pending = cancelledCache.GetAsync(path, 19, stamp, cancellation.Token); await Await(started);
        cancellation.Cancel(); release.SetResult(true);
        try { await pending; throw new Exception("Cancelled read returned data"); } catch (OperationCanceledException) { }
    }
    await cancelledCache.GetAsync(path, 19, stamp, default);
    Check(reads == 2, "A loader ignoring cancellation cannot commit a cancelled result to cache");

    var loaders = new ConcurrentDictionary<int, TaskCompletionSource<int>>();
    var tokens = new ConcurrentDictionary<int, CancellationToken>();
    var published = new ConcurrentQueue<int>();
    var entered = new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true };
    var notifications = System.Threading.Channels.Channel.CreateUnbounded<int>(entered);
    var active = 0; var maximum = 0;
    var completions = System.Threading.Channels.Channel.CreateUnbounded<int>();
    using (var work = new VisibleMediaWork<int, int>(async (key, token) =>
    {
        var count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
        tokens[key] = token;
        var completion = loaders.GetOrAdd(key, _ => Signal<int>()); notifications.Writer.TryWrite(key);
        try { return await completion.Task; } finally { Interlocked.Decrement(ref active); }
    }, (key, _) => { published.Enqueue(key); completions.Writer.TryWrite(key); }, concurrency: 1))
    {
        work.Update([1]); Check(await notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) == 1, "Visible item starts background work");
        work.Update([1, 2]); work.Update([3]);
        Check(tokens[1].IsCancellationRequested, "Leaving the viewport cancels running work");
        loaders[1].SetResult(1);
        Check(await notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) == 3 && !loaders.ContainsKey(2), "Cancelled queued requests never invoke the loader");
        loaders[3].SetResult(3);
        Check(await completions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) == 3, "Only the current viewport publishes its completion");
        Check(maximum == 1 && !published.Contains(1) && work.TryGetResult(3, out var value) && value == 3, "Concurrency stays bounded and stale completion is discarded");
        work.Update([3]); Check(loaders.Count == 2, "Completed visible requests are retained for recreated controls");
        work.Update([]); Check(!work.TryGetResult(3, out _), "Leaving the viewport releases visible results");
        work.Update([4]); await notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        work.Dispose(); Check(tokens[4].IsCancellationRequested, "Disposing the view cancels outstanding work"); loaders[4].SetResult(4);
    }

    // A removed item may become visible again before its old, uncooperative loader exits.
    var oldStarted = Signal<bool>(); var replacementStarted = Signal<bool>();
    var oldResult = Signal<int>(); var replacementResult = Signal<int>(); var replacementPublished = Signal<int>();
    var generations = 0;
    using (var replacement = new VisibleMediaWork<int, int>(async (_, _) =>
    {
        if (Interlocked.Increment(ref generations) == 1) { oldStarted.SetResult(true); return await oldResult.Task; }
        replacementStarted.SetResult(true); return await replacementResult.Task;
    }, (_, result) => replacementPublished.TrySetResult(result)))
    {
        replacement.Update([7]); await Await(oldStarted); replacement.Update([]); replacement.Update([7]); await Await(replacementStarted);
        oldResult.SetResult(99); replacementResult.SetResult(7);
        Check(await Await(replacementPublished) == 7 && replacement.TryGetResult(7, out var current) && current == 7,
            "An old generation cannot publish over a replacement for the same path");
    }

    var cacheStarts = System.Threading.Channels.Channel.CreateUnbounded<string>();
    var cacheRelease = Signal<bool>(); var cacheActive = 0;
    var bounded = new FilePickerMediaCache(async (filePath, token) =>
    {
        Interlocked.Increment(ref cacheActive); cacheStarts.Writer.TryWrite(filePath);
        try { await cacheRelease.Task.WaitAsync(token); return new(3); }
        finally { Interlocked.Decrement(ref cacheActive); }
    }, Path.Combine(root, "bounded"), maxConcurrency: 2);
    var pendingOne = bounded.GetAsync(path, 19, stamp, default);
    var pendingTwo = bounded.GetAsync(secondPath, 20, stamp, default);
    await cacheStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    await cacheStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    using (var queuedCancellation = new CancellationTokenSource())
    {
        var pendingThree = bounded.GetAsync(Path.Combine(root, "third.mp4"), 21, stamp, queuedCancellation.Token);
        Check(cacheActive == 2 && !cacheStarts.Reader.TryRead(out _), "The shared cache admits at most two probes");
        queuedCancellation.Cancel();
        try { await pendingThree; throw new Exception("Queued cache read was not cancelled"); } catch (OperationCanceledException) { }
        Check(!cacheStarts.Reader.TryRead(out _), "Cancelling a cache request waiting for a slot never starts its probe");
    }
    cacheRelease.SetResult(true); await Task.WhenAll(pendingOne, pendingTwo);

    var state = new FilePickerStateStore(Path.Combine(root, "state"));
    var paths = Enumerable.Range(0, 70).Select(index => Path.Combine(root, index + ".mp4")).ToArray();
    foreach (var item in paths) await File.WriteAllTextAsync(item, "x");
    var folders = Enumerable.Range(0, 20).Select(index => Path.Combine(root, "folder-" + index)).ToArray();
    foreach (var folder in folders) Directory.CreateDirectory(folder);
    await state.RecordAsync(paths, folders); await state.FavoriteAsync(folders[0], true); await state.SetGridAsync(false);
    var history = await new FilePickerStateStore(Path.Combine(root, "state")).LoadAsync();
    Check(history.Files.Count == 64 && history.Folders.Count == 16 && !history.Grid && history.Favorites.Count == 1, "History bounds, favorites and mode survive restart");
    var deleted = history.Files[0].Path; File.Delete(deleted); Directory.Delete(folders[0]);
    history = await state.LoadAsync();
    Check(!history.Files.Any(visit => visit.Path == deleted) && history.Favorites.Count == 0, "Missing files and folders are removed from persistent history");
    await state.FavoriteAsync(folders[1], true); await state.FavoriteAsync(folders[1], false);
    Check((await state.LoadAsync()).Favorites.Count == 0, "Favorite removal is persisted");
    Console.WriteLine("All picker cache and cancellation checks passed.");
}
finally { Directory.Delete(root, true); }
