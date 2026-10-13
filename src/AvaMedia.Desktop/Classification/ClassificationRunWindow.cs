using System.ComponentModel;
using AvaMedia.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using static AvaMedia.Desktop.Classification.ClassifyUi;

namespace AvaMedia.Desktop.Classification;

/// <summary>Detail window of one AI 媒体整理 task: live pipeline while running, before/after review when finished.</summary>
public sealed partial class ClassificationRunWindow : Window
{
    private readonly Job _job;
    private readonly Action<Job>? _pause, _stop;
    private readonly Action<Job, string> _openAction;
    private readonly FolderClassificationProgress _progress = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Grid _body = new();
    private readonly TextBlock _title = T("", 13, FontWeight.Bold, raw: true), _subtitle = T("", 12, brush: Muted, raw: true);
    private readonly StackPanel _headerActions = new() { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = Resource("AcGap"), VerticalAlignment = VerticalAlignment.Center };
    private int _dirty = 1, _computing;
    private long _lastApplied;
    private bool? _resultMode;
    private bool _closed;
    public static int Applied, Computed; public static double LastComputeMs, MaxComputeMs, MaxApplyMs;

    public ClassificationRunWindow(Job job, Action<Job>? pause, Action<Job>? stop, Action<Job, string> openAction)
    {
        ApplyToolStyles(this);
        _job = job; _pause = pause; _stop = stop; _openAction = openAction;
        Title = L("AI 媒体整理") + " · " + job.Name; Width = 1280; Height = 800; MinWidth = 1040; MinHeight = 640;
        Bind(BackgroundProperty, Resource(Page)); WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "gear");
        var header = new Grid { ColumnDefinitions = new("*,Auto"), [!Grid.ColumnSpacingProperty] = Resource("AcGap"), Height = 48, Margin = new(0, 0, 16, 0) };
        var titles = new Grid { ColumnDefinitions = new("Auto,*"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap"), VerticalAlignment = VerticalAlignment.Center };
        _title.VerticalAlignment = VerticalAlignment.Center; _subtitle.VerticalAlignment = VerticalAlignment.Center;
        titles.Children.Add(_title); Grid.SetColumn(_subtitle, 1); titles.Children.Add(_subtitle);
        header.Children.Add(titles); Grid.SetColumn(_headerActions, 1); header.Children.Add(_headerActions);
        Content = WindowChrome.WrapDialog(this, _body, header);
        job.PropertyChanged += JobChanged;
        // Coalesce notifications to at most four applications per second; compute immutable snapshots off the UI thread.
        _timer.Tick += (_, _) => Pump();
        Opened += async (_, _) =>
        {
            if (job.ClassificationSnapshot is null)
            {
                try { job.InitializeClassificationSnapshot(await Task.Run(() => FolderClassificationTaskStore.LoadAsync(job))); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            }
            job.ClassificationRegroup ??= await Task.Run(() => FolderClassificationTaskStore.LoadRegroup(job));
            _timer.Start(); Pump();
        };
        if (Environment.GetEnvironmentVariable("AVAMEDIA_UI_LAG_LOG") is { Length: > 0 } lagLog) StartLagProbe(lagLog);
        Closed += (_, _) => { _closed = true; _timer.Stop(); job.PropertyChanged -= JobChanged; if (Volatile.Read(ref _computing) == 0) DisposeRetiredPreviews(); };
    }

    internal Job Job => _job;

    /// <summary>Diagnostics only: samples UI-thread latency with a 50 ms timer and writes percentiles to a file.</summary>
    private void StartLagProbe(string path)
    {
        var lags = new List<double>(); var clock = System.Diagnostics.Stopwatch.StartNew(); var last = 0d;
        var probe = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) =>
        {
            var now = clock.Elapsed.TotalMilliseconds; if (last > 0) lags.Add(Math.Max(0, now - last - 50)); last = now;
            if (lags.Count == 0 || lags.Count % 40 != 0) return;
            var sorted = lags.OrderBy(x => x).ToArray();
            try { File.WriteAllText(path, $"samples {sorted.Length} p50 {sorted[sorted.Length / 2]:0.0} ms p95 {sorted[(int)(sorted.Length * .95)]:0.0} ms max {sorted[^1]:0.0} ms; applies {Applied} computes {Computed} maxCompute {MaxComputeMs:0.0} ms maxApply {MaxApplyMs:0.0} ms; files {_progress.Files.Count} done {_progress.Completed}\n"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("lag probe: " + error.Message); }
        });
        probe.Start(); Closed += (_, _) => probe.Stop();
    }
    private void JobChanged(object? sender, PropertyChangedEventArgs e) => Interlocked.Exchange(ref _dirty, 1);
    internal void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    private void Pump()
    {
        if (_closed || _lastApplied != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastApplied).TotalMilliseconds < 250) return;
        if (Interlocked.CompareExchange(ref _dirty, 0, 1) != 1) return;
        if (Interlocked.Exchange(ref _computing, 1) == 1) { Interlocked.Exchange(ref _dirty, 1); return; }
        _ = Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { _progress.Update(_job); ProjectRun(); }
            catch (Exception) { }
            watch.Stop(); LastComputeMs = watch.Elapsed.TotalMilliseconds; MaxComputeMs = Math.Max(MaxComputeMs, LastComputeMs);
            Interlocked.Increment(ref Computed);
            Dispatcher.UIThread.Post(() =>
            {
                try { if (!_closed) Apply(); }
                finally { DisposeRetiredPreviews(); _lastApplied = System.Diagnostics.Stopwatch.GetTimestamp(); Interlocked.Exchange(ref _computing, 0); }
            }, DispatcherPriority.Background);
        });
    }

    private bool Active => _job.State is JobState.Waiting or JobState.Running or JobState.Paused or JobState.Stopping;

    private void Apply()
    {
        Interlocked.Increment(ref Applied);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { ApplyCore(); }
        finally { MaxApplyMs = Math.Max(MaxApplyMs, watch.Elapsed.TotalMilliseconds); }
    }

    private void ApplyCore()
    {
        var result = !Active;
        if (_resultMode != result)
        {
            _resultMode = result; _body.Children.Clear();
            _body.Children.Add(result ? _resultView ??= BuildResult() : _runView ??= BuildRun());
        }
        _title.Text = L("AI 媒体整理") + " · " + _job.Name;
        if (result)
        {
            _subtitle.Text = $"{L("用时")} {Clock(Elapsed())} · {L("完成")} {_progress.Completed} {L("个")}，{L("待确认")} {_progress.Pending} {L("个")}，{L("失败")} {_progress.Failed} {L("个")}";
            RenderResult();
        }
        else
        {
            _subtitle.Text = L("从任务列表「查看任务」打开 · 关闭窗口后任务继续运行");
            RenderRun();
        }
        RenderHeaderActions(result);
    }

    private TimeSpan Elapsed() => _progress.Elapsed;

    private string? _headerKey;
    private void RenderHeaderActions(bool result)
    {
        var key = result ? "result:" + string.Join(",", _job.Options.FolderClassification?.Rules.Select(r => r.Id) ?? []) : "run:" + _job.State;
        if (key == _headerKey) return; _headerKey = key;
        _headerActions.Children.Clear();
        _headerActions.Children.Add(_percent);
        _percent.Text = $"{_progress.Percent:0}%";
        if (_job.State is JobState.Failed or JobState.Cancelled)
        {
            _headerActions.Children.Add(new StatusPill { Text = L(_job.State == JobState.Failed ? "失败" : "已取消"), State = _job.State.ToString().ToLowerInvariant() });
            _headerActions.Children.Add(Pill("查看日志", click: async () => await Ui.MessageDialog(this, L("处理日志"), L(_job.Status), [await _job.ReadLogAsync()])));
            _headerActions.Children.Add(Pill("关闭", click: Close));
            return;
        }
        if (result)
        {
            var rules = _job.Options.FolderClassification?.Rules ?? [];
            if (rules.Length > 1)
                _headerActions.Children.Add(Segmented(rules.Select(rule => rule.Name).ToArray(), Math.Max(0, Array.FindIndex(rules, r => r.Id == _resultRule)),
                    index => { _resultRule = rules[index].Id; _resultKey = null; RenderResult(); }));
            return;
        }
        if (_pause is not null && _job.State is JobState.Waiting or JobState.Running or JobState.Paused)
            _headerActions.Children.Add(Pill(_job.State == JobState.Paused ? "继续任务" : "临时停止", _job.State == JobState.Paused ? "play" : "pause", click: () => _pause(_job)));
        if (_stop is not null && _job.State is JobState.Waiting or JobState.Running or JobState.Paused)
        {
            _headerActions.Children.Add(Pill("结束任务", "square", "danger", () => _stop(_job)));
        }
    }
}
