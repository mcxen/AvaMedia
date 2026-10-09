using Avalonia.Controls;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Notifications;

namespace AvaMedia.Desktop;

internal sealed class ModelDownloadState(DownloadableModel model, Window? owner, ModelSourcePreference sourcePreference)
{
    public DownloadableModel Model { get; } = model;
    public ModelSourcePreference SourcePreference { get; } = sourcePreference;
    public string NotificationKey { get; } = "model:" + model.Id + ":" + Guid.NewGuid().ToString("N");
    public WeakReference<Window>? Owner { get; } = owner is null ? null : new(owner);
    public CancellationTokenSource? Cancellation { get; set; }
    public ModelDownloadProgress? Progress { get; set; }
    public string? Outcome { get; set; }
    public string? Error { get; set; }
    public bool Active => Cancellation is not null;
    public bool CancellationRequested => Cancellation?.IsCancellationRequested == true;
}

/// <summary>Application-owned model downloads. Windows only observe state and request start or cancellation.</summary>
internal sealed class ModelDownloads
{
    public static ModelDownloads Shared { get; } = new();
    private readonly ModelStore _store = new();
    private readonly Dictionary<string, ModelDownloadState> _states = [];
    private bool _stopped;
    public event Action<string>? Changed;
    public ModelDownloadState? Find(string id) => _states.GetValueOrDefault(id);
    public bool CanStart(string id) => !_stopped && ModelCatalog.Find(id).Supported && Find(id)?.Active != true
        && !_store.IsBusy(id) && (id != ModelCatalog.LamaId || !ModelInstallation.Installing);

    public void Start(DownloadableModel model, Window? owner = null, ModelSourcePreference? sourcePreference = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanStart(model.Id)) return;
        sourcePreference ??= ModelDownloadSources.Preference;
        sourcePreference.Validate();
        var state = new ModelDownloadState(model, owner, sourcePreference)
        {
            Cancellation = new(), Progress = new(_store.DownloadedBytes(model.Id), model.DownloadSize, "校验模型")
        };
        _states[model.Id] = state;
        NotifyChanged(model.Id); PublishProgress(state);
        _ = DownloadAsync(state);
    }

    public void Cancel(string id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Find(id) is not { Active: true } state || state.CancellationRequested) return;
        state.Cancellation!.Cancel();
        state.Progress = (state.Progress ?? new(0, state.Model.DownloadSize, "正在停止…")) with { Stage = "正在停止…" };
        NotifyChanged(id); PublishProgress(state);
    }

    public void ClearResult(string id)
    { if (Find(id)?.Active != true) _states.Remove(id); }

    private async Task DownloadAsync(ModelDownloadState state)
    {
        var cancellation = state.Cancellation!; var model = state.Model;
        var progress = new Progress<ModelDownloadProgress>(value =>
        {
            if (_stopped || !state.Active || state.CancellationRequested || Find(model.Id) != state) return;
            state.Progress = value; NotifyChanged(model.Id); PublishProgress(state);
        });
        try
        {
            await Task.Run(() => _store.DownloadAsync(model.Id, progress, cancellation.Token, state.SourcePreference), cancellation.Token);
            state.Outcome = "已下载";
            if (model.Id == ModelCatalog.LamaId) ModelInstallation.ClearFailure();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { state.Outcome = "已停止"; }
        catch (Exception error)
        {
            state.Outcome = "下载失败"; state.Error = error.Message;
            AppDiagnostics.Record("Model download " + model.Id, error);
        }
        finally
        {
            state.Cancellation = null; state.Progress = null; cancellation.Dispose();
            if (!_stopped) NotifyChanged(model.Id);
        }
        if (_stopped) return;
        if (state.Error is { } errorMessage)
            NotificationCenter.Shared.Publish(Owner(state), new(state.NotificationKey, "模型操作失败",
                (FormattableString)$"{model.Name}\n{errorMessage}", NotificationKind.Error, [
                    new("重试", () => { Start(model, Owner(state), state.SourcePreference); return Task.CompletedTask; }, Primary: true, DismissOnSuccess: true,
                        Enabled: () => CanStart(model.Id)),
                    new("模型管理", ModelNotifications.OpenManagementAsync)]));
        else if (state.Outcome == "已停止")
            NotificationCenter.Shared.Publish(Owner(state), new(state.NotificationKey, "模型操作已停止", model.Name), show: false);
        else
            NotificationCenter.Shared.Publish(Owner(state), new(state.NotificationKey, "模型操作完成",
                (FormattableString)$"{model.Name} · {Localization.Key(state.Outcome!)}", NotificationKind.Success,
                [new("模型管理", ModelNotifications.OpenManagementAsync)]));
    }

    private void PublishProgress(ModelDownloadState state)
    {
        if (_stopped || state.Progress is not { } value) return;
        NotificationCenter.Shared.Publish(Owner(state), new(state.NotificationKey, "下载模型",
            value.SourceName.Length > 0
                ? (FormattableString)$"{state.Model.Name}\n{Localization.Key(value.Stage)} · {value.Received / 1048576d:0.0} / {value.Total / 1048576d:0.0} MB · {value.SourceName}"
                : $"{state.Model.Name}\n{Localization.Key(value.Stage)} · {value.Received / 1048576d:0.0} / {value.Total / 1048576d:0.0} MB",
            NotificationKind.Progress,
            [new("停止下载", () => { Cancel(state.Model.Id); return Task.CompletedTask; },
                Enabled: () => state.Active && !state.CancellationRequested)],
            value.Total > 0 ? value.Percent : null, value.Total <= 0));
    }

    private static Window? Owner(ModelDownloadState state) => state.Owner?.TryGetTarget(out var owner) == true
        && NotificationCenter.Available(owner) ? owner : null;

    private void NotifyChanged(string id)
    {
        if (Changed is not { } changed) return;
        foreach (Action<string> subscriber in changed.GetInvocationList())
        {
            try { subscriber(id); }
            catch (Exception error) { AppDiagnostics.Record("Model download status", error); }
        }
    }

    public void Shutdown()
    {
        _stopped = true;
        foreach (var state in _states.Values) state.Cancellation?.Cancel();
    }
}
