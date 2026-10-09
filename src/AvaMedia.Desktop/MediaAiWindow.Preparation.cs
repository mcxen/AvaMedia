using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly TextBlock _warmStatus = Ui.Text("", "caption");
    private readonly Button _warmRetry = new() { Content = "重试", IsVisible = false };
    private CancellationTokenSource? _warmRequest;

    private Task PrepareModelsAsync(bool reset = false)
    {
        if (_closed || _busy) return Task.CompletedTask;
        _warmRequest?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _warmRequest = request;
        var options = new MediaTagOptions(PreferGpu: _gpu.IsChecked == true, BatchSize: 1, RecognizeScenes: _sceneTags.IsChecked == true)
        {
            SemanticCandidates = SemanticLibraryCandidates,
            RecognizeNsfw = _realPeople.IsChecked == true
        };
        return PrepareModelsAsync(options, request, reset);
    }

    private async Task PrepareModelsAsync(MediaTagOptions options, CancellationTokenSource request, bool reset)
    {
        using (request)
        {
            _warmRetry.IsVisible = false;
            _warmStatus.Text = Localization.Text("准备本地模型"); _warmStatus.IsVisible = true;
            ToolTip.SetTip(_warmStatus, null);
            var progress = new Progress<AiActivity>(value =>
            {
                if (_closed || _warmRequest != request || request.IsCancellationRequested) return;
                _warmStatus.Text = Localization.Text(value.Stage)
                    + (value.Current is { } current && value.Total is > 0 and var total ? $" · {current:0}/{total:0}" : "");
            });
            try
            {
                if (reset) await _tagService.ResetPreparedModelsAsync(request.Token);
                var ready = await _tagService.WarmAsync(options, progress, request.Token);
                if (_closed || _warmRequest != request || request.IsCancellationRequested) return;
                _warmStatus.Text = ready ? Localization.Text(_modelStatus.IsVisible ? "标签模型已预热" : "模型已就绪") : "";
                _warmStatus.IsVisible = ready && _modelStatus.IsVisible;
                if (ready && !_modelStatus.IsVisible && !_busy && _status.Text == Localization.Text("就绪"))
                    _status.Text = Localization.Text("模型已就绪");
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (_closed || _warmRequest != request) return;
                _warmStatus.Text = Localization.Text("模型准备失败");
                ToolTip.SetTip(_warmStatus, error.Message); _warmRetry.IsVisible = true;
                AppDiagnostics.Record("AI model preparation", error);
            }
            finally
            {
                if (_warmRequest == request) { _warmRequest = null; _warmRetry.IsEnabled = !_busy; }
            }
        }
    }
}
