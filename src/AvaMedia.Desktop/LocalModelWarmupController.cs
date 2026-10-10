using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <summary>Window-owned scheduling only; the prepared processes belong to the application cache.</summary>
internal sealed class LocalModelWarmupController
{
    private readonly IMediaEngine _engine;
    private readonly AppSettings _settings;
    private readonly ModelStore _store = new();
    private readonly LocalModelWarmupBudget _budget;
    private CancellationTokenSource? _request;
    private string[] _ids = [];
    private string? _source;
    private bool _gpu, _opened, _closed, _handoff;
    private string _plan = "";
    private bool _selectionPrepared;
    private bool _stopped, _manual;
    public bool Running => _request is not null;
    public bool Stopped => _stopped;
    public Exception? Error { get; private set; }
    public event Action? Changed;

    public LocalModelWarmupController(Window window, IMediaEngine engine, AppSettings settings)
    {
        _engine = engine; _settings = settings; _budget = new(settings);
        window.Opened += (_, _) => { _opened = true; Start(); };
        window.Closed += (_, _) => { _closed = true; if (!_handoff) _request?.Cancel(); };
    }
    public void HandOff() { _handoff = true; _budget.Promote(); }
    public void Stop()
    {
        _stopped = true; var request = _request; _request = null; request?.Cancel(); Changed?.Invoke();
    }
    public void Retry()
    {
        Stop(); _manual = true; _stopped = false; _selectionPrepared = false; Error = null; Start();
    }
    public void RefreshModels() { _selectionPrepared = false; if (_opened) Start(); }
    public void Update(IEnumerable<string> ids, bool gpu, string? source)
    {
        if (_closed) return;
        var needed = ids.Distinct().Where(ModelCatalog.RequiresSummaryRuntime).ToArray();
        var plan = string.Join('|', needed) + "|" + gpu;
        _source = source; _budget.SelectMedia(source is not null);
        if (plan != _plan)
        {
            _plan = plan; _ids = needed; _gpu = gpu; _selectionPrepared = false; _stopped = _manual = _handoff = false; Error = null;
            _request?.Cancel(); _request = null;
        }
        if (_opened) Start();
    }
    private void Start()
    {
        if (_closed || !_opened || _ids.Length == 0 || _request is not null || _stopped || (!_manual && !_settings.PrewarmLocalModels)) return;
        // Once a plan is ready, source changes do not trigger repeated speculative inference.
        if (_selectionPrepared) return;
        var request = new CancellationTokenSource(); _request = request;
        Changed?.Invoke();
        var ids = _ids; var gpu = _gpu;
        _ = RunAsync(ids, gpu, request);
    }
    private async Task RunAsync(string[] ids, bool gpu, CancellationTokenSource request)
    {
        var selected = false;
        try
        {
            await Task.Run(async () =>
            {
                foreach (var id in ids)
                    await LocalSummaryModelCache.WarmAsync(_store, id, gpu, _budget, request.Token).ConfigureAwait(false);
            }, request.Token);
            if (_request != request || _closed) return;
            if (_source is { } source)
            {
                selected = true;
                foreach (var id in ids.Where(ModelCatalog.IsSummaryVision))
                    await Task.Run(() => LocalSummaryModelCache.WarmAsync(_store, id, gpu, _budget, request.Token,
                        token => _engine.Thumbnail(source, 0, 768, 768, token, pad: false)), request.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_request == request) Error = error; AppDiagnostics.Record("Local model preparation", error); }
        finally
        {
            if (_request == request) { _request = null; _selectionPrepared = selected; if (!_closed) Changed?.Invoke(); }
            request.Dispose();
        }
    }
}
