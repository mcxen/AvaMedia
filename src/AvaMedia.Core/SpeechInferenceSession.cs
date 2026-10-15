using AvaMedia.Core.Compute;
using Whisper.net.Logger;
using System.Runtime.CompilerServices;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace AvaMedia.Core;

/// <summary>GPU-first speech recognition, with CPU VAD on Metal and cancellation preserved.</summary>
internal sealed class SpeechInferenceSession : IDisposable
{
    private readonly string _model;
    private readonly Func<WhisperFactory, WhisperProcessor> _configure;
    private readonly int _threads;
    private readonly Action<string, string?> _status;
    private readonly Action<string>? _loading;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private WhisperVadFactory? _vadFactory;
    private WhisperVadProcessor? _vad;
    // Metal VAD in the bundled native runtime aborts inside GGML before a managed retry can run.
    private bool _speechGpu = true, _vadGpu = !OperatingSystem.IsMacOS();
    private readonly ComputeRoute _route;
    private readonly IDisposable _nativeLog;
    private bool _metalObserved;
    private string? _fallbackReason;

    public SpeechInferenceSession(string model, Func<WhisperFactory, WhisperProcessor> configure, int threads,
        Action<string, string?> status, Action<string>? loading = null)
    {
        _model = model; _configure = configure; _threads = threads; _status = status; _loading = loading;
        var preference = ComputeSettings.CurrentPreference;
        _route = WhisperCompute.Prepare(preference);
        _speechGpu = _route.Chosen != ComputeBackend.Cpu;
        _vadGpu = _speechGpu && !OperatingSystem.IsMacOS();
        _nativeLog = LogProvider.AddLogger((level, message) =>
        {
            if (message is null) return;
            if (message.Contains("using Metal", StringComparison.OrdinalIgnoreCase) || message.Contains("using device Metal", StringComparison.OrdinalIgnoreCase)) _metalObserved = true;
            if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) || message.Contains("no GPU", StringComparison.OrdinalIgnoreCase))
                ComputeRuntimeFormatter.Log("Whisper 原生运行库：" + message);
        });
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
        _loading?.Invoke("Whisper");
        _processor?.Dispose(); _processor = null;
        _factory?.Dispose(); _factory = null;
        _processor = WhisperCompute.WithRuntimeOrder(_route, () =>
        {
            _factory = WhisperFactory.FromPath(_model, new() { UseGpu = _speechGpu });
            return _configure(_factory);
        });
    }

    private void LoadVad()
    {
        _loading?.Invoke("Silero VAD");
        _vad?.Dispose(); _vad = null;
        _vadFactory?.Dispose(); _vadFactory = null;
        _vadFactory = WhisperVadFactory.FromPath(SpeechAssets.EnsureVadModel());
        _vad = _vadFactory.CreateBuilder().WithUseGpu(_vadGpu).WithThreads(_threads).WithThreshold(.5f)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250)).WithMinSilenceDuration(TimeSpan.FromMilliseconds(150))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(100)).Build();
    }

    public string Backend => $"Whisper: {ComputeRuntimeFormatter.Name(WhisperCompute.ActualBackend(RuntimeOptions.LoadedLibrary, _speechGpu, ComputeEnvironment.Platform, _metalObserved))} · Silero: {(_vadGpu && WhisperCompute.ActualBackend(RuntimeOptions.LoadedLibrary, _vadGpu, ComputeEnvironment.Platform, _metalObserved) != ComputeBackend.Cpu ? RuntimeOptions.LoadedLibrary : "CPU")} · 运行库 {RuntimeOptions.LoadedLibrary}";

    private void Report(string? reason = null)
    {
        _fallbackReason = reason ?? _fallbackReason ?? WhisperCompute.MissingGpuReason(_route, RuntimeOptions.LoadedLibrary, _speechGpu, _metalObserved);
        var actual = WhisperCompute.ActualBackend(RuntimeOptions.LoadedLibrary, _speechGpu, ComputeEnvironment.Platform, _metalObserved);
        while (_route.Chosen != actual && _route.Chosen != ComputeBackend.Cpu)
            _route.ReportFailure(_route.Chosen, _fallbackReason ?? "此运行库未实际加载。");
        ComputeJobContext.Report(_route.Outcome(RuntimeOptions.LoadedLibrary?.ToString() ?? "未知运行库") with { ActualBackend = actual });
        var note = _fallbackReason;
        if (OperatingSystem.IsMacOS()) note = (note is null ? "" : note + "；") + "当前原生运行时的 Silero Metal VAD 会中止进程，使用 CPU 检测语音。";
        ComputeRuntimeFormatter.Log("计算后端：" + Backend + (note is null ? "" : "；" + note));
        _status(Backend, note);
    }

    private void RetrySpeech(Exception error)
    {
        if (_route.Chosen != ComputeBackend.Cpu) _route.ReportFailure(_route.Chosen, "Whisper 模型加载或推理失败：" + error.Message);
        _speechGpu = false; Report("Whisper GPU 失败，回退 CPU：" + error.Message); LoadSpeech();
    }

    private void RetryVad(Exception error)
    {
        _vadGpu = false; Report("Silero GPU 失败，回退 CPU：" + error.Message); LoadVad();
    }

    public async Task<IReadOnlyList<VadSegmentData>> DetectSpeechAsync(Stream audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Report();
        await CpuResourcePolicy.YieldAsync(ct).ConfigureAwait(false);
        var start = audio.Position;
        try { return await _vad!.DetectSpeechAsync(audio, ct).ConfigureAwait(false); }
        catch (WhisperProcessingException error) when (_vadGpu && !ct.IsCancellationRequested)
        {
            RetryVad(error); audio.Position = start; ct.ThrowIfCancellationRequested();
            return await _vad!.DetectSpeechAsync(audio, ct).ConfigureAwait(false);
        }
    }

    public (string Language, double Confidence) DetectLanguage(string wav, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(wav); using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("语言检测需要 PCM WAV。");
        reader.ReadInt32(); if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("WAV 头无效。");
        while (stream.Position + 8 <= stream.Length)
        {
            var name = new string(reader.ReadChars(4)); var size = reader.ReadUInt32();
            if (name == "data")
            {
                var samples = new float[(int)Math.Min(480000, size / 2)];
                for (int i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16() / 32768f;
                var (language, probability) = _processor!.DetectLanguageWithProbability(samples);
                ct.ThrowIfCancellationRequested(); return (language ?? "auto", probability);
            }
            stream.Seek(size + size % 2, SeekOrigin.Current);
        }
        return ("auto", 0);
    }
    public void ChangeLanguage(string language) => _processor!.ChangeLanguage(language);

    public async IAsyncEnumerable<SegmentData> ProcessAsync(Stream audio, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Report();
        await CpuResourcePolicy.YieldAsync(ct).ConfigureAwait(false);
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
        _nativeLog.Dispose();
        _vad?.Dispose(); _vadFactory?.Dispose(); _processor?.Dispose(); _factory?.Dispose();
    }
}
