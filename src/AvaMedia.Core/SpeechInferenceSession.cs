using System.Runtime.CompilerServices;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace AvaMedia.Core;

/// <summary>GPU-first Whisper and Silero contexts, each with one CPU retry and cancellation preserved.</summary>
internal sealed class SpeechInferenceSession : IDisposable
{
    private readonly string _model;
    private readonly Func<WhisperFactory, WhisperProcessor> _configure;
    private readonly int _threads;
    private readonly Action<string, string?> _status;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private WhisperVadFactory? _vadFactory;
    private WhisperVadProcessor? _vad;
    private bool _speechGpu = true, _vadGpu = true;

    public SpeechInferenceSession(string model, Func<WhisperFactory, WhisperProcessor> configure, int threads,
        Action<string, string?> status)
    {
        _model = model; _configure = configure; _threads = threads; _status = status;
        try
        {
            try { LoadSpeech(); }
            catch (WhisperModelLoadException error) { RetrySpeech(error); }
            try { LoadVad(); }
            catch (WhisperModelLoadException error) { RetryVad(error); }
            Report();
        }
        catch { Dispose(); throw; }
    }

    private void LoadSpeech()
    {
        _processor?.Dispose(); _processor = null;
        _factory?.Dispose(); _factory = null;
        _factory = WhisperFactory.FromPath(_model, new() { UseGpu = _speechGpu });
        _processor = _configure(_factory);
    }

    private void LoadVad()
    {
        _vad?.Dispose(); _vad = null;
        _vadFactory?.Dispose(); _vadFactory = null;
        _vadFactory = WhisperVadFactory.FromPath(SpeechAssets.EnsureVadModel());
        _vad = _vadFactory.CreateBuilder().WithUseGpu(_vadGpu).WithThreads(_threads).WithThreshold(.5f)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250)).WithMinSilenceDuration(TimeSpan.FromMilliseconds(150))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(100)).Build();
    }

    private void Report(string? reason = null) => _status(
        $"Whisper: {(_speechGpu ? "GPU 优先" : "CPU")} · Silero: {(_vadGpu ? "GPU 优先" : "CPU")} · {RuntimeOptions.LoadedLibrary}", reason);

    private void RetrySpeech(Exception error)
    {
        _speechGpu = false; Report("Whisper GPU 失败，回退 CPU：" + error.Message); LoadSpeech();
    }

    private void RetryVad(Exception error)
    {
        _vadGpu = false; Report("Silero GPU 失败，回退 CPU：" + error.Message); LoadVad();
    }

    public async Task<IReadOnlyList<VadSegmentData>> DetectSpeechAsync(Stream audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = audio.Position;
        try { return await _vad!.DetectSpeechAsync(audio, ct).ConfigureAwait(false); }
        catch (WhisperProcessingException error) when (_vadGpu && !ct.IsCancellationRequested)
        {
            RetryVad(error); audio.Position = start; ct.ThrowIfCancellationRequested();
            return await _vad!.DetectSpeechAsync(audio, ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<SegmentData> ProcessAsync(Stream audio, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = audio.Position;
        var emittedUntil = TimeSpan.Zero;
        var reader = _processor!.ProcessAsync(audio, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool available;
                try { available = await reader.MoveNextAsync().ConfigureAwait(false); }
                catch (WhisperProcessingException error) when (_speechGpu && !ct.IsCancellationRequested)
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                    RetrySpeech(error); audio.Position = start; ct.ThrowIfCancellationRequested();
                    reader = _processor!.ProcessAsync(audio, ct).GetAsyncEnumerator(ct);
                    continue;
                }
                if (!available) break;
                var segment = reader.Current;
                // A CPU retry must not republish subtitle segments already delivered by the GPU attempt.
                if (segment.End <= emittedUntil) continue;
                emittedUntil = segment.End;
                yield return segment;
            }
        }
        finally { await reader.DisposeAsync().ConfigureAwait(false); }
    }

    public void Dispose()
    {
        _vad?.Dispose(); _vadFactory?.Dispose(); _processor?.Dispose(); _factory?.Dispose();
    }
}
