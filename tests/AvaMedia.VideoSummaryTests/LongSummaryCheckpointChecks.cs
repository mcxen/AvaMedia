using AvaMedia.Core;

internal static class LongSummaryCheckpointChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aisum-checkpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SummaryCheckpointStore(root); int calls = 0;
            async Task Run(int fail)
            {
                for (int i = 0; i < 36; i++)
                {
                    var block = i;
                    await store.GetOrCreateAsync($"asr/chunk-{i:0000}.json", "input-v1", _ =>
                    { if (block == fail) throw new IOException("simulated crash"); calls++; return Task.FromResult(new SpeechChunk([], [], "en")); }, default);
                }
            }
            try { await Run(11); } catch (IOException) { }
            LongSummaryPhaseChecks.Check(calls == 11, "checkpoint before crash");
            calls = 0; store = new SummaryCheckpointStore(root); await Run(-1);
            LongSummaryPhaseChecks.Check(calls == 25, "only remaining 300-second chunks recognized");
            LongSummaryPhaseChecks.Check(await store.ReadAsync<SpeechChunk>("asr/chunk-0000.json", "changed") is null, "hash invalidation");
            await File.WriteAllTextAsync(store.PathFor("asr/chunk-0000.json"), "corrupt");
            LongSummaryPhaseChecks.Check(await store.ReadAsync<SpeechChunk>("asr/chunk-0000.json", "input-v1") is null, "corrupt checkpoint rejected");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await store.WriteAsync("cancelled.json", "hash", new SpeechChunk([], [], "ja"), cancelled.Token); } catch (OperationCanceledException) { }
            LongSummaryPhaseChecks.Check(!File.Exists(store.PathFor("cancelled.json")) && !Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "cancelled atomic write");
            LongSummaryPhaseChecks.Check(ShotDetector.Difference(new byte[12], Enumerable.Repeat((byte)255, 12).ToArray()) == 1, "shot difference");
            Console.WriteLine("PASS Phase 2: crash/resume 11+25 chunks, hash/corruption/cancellation, shot score");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
