using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly NsfwReviewStore _reviewStore = new();
    private bool _restoringReview, _reviewStorageErrorShown;

    private void RestoreReview(MediaFileEntry entry, MediaTagResult result)
    {
        if (entry.ReviewLoaded) return;
        entry.ReviewLoaded = true;
        try
        {
            var note = _reviewStore.Find(result);
            if (note is null) return;
            _restoringReview = true; entry.RestoreReview(note);
        }
        catch (Exception error) { ReportReviewStorageError(error); }
        finally { _restoringReview = false; }
    }

    private async Task SaveReviewAsync(MediaFileEntry entry)
    {
        if (_restoringReview || !entry.HasTagResult || !_results.TryGetValue(entry.Path, out var result)) return;
        var decision = entry.ReviewDecision; var updated = entry.ReviewedAtUtc ?? DateTime.UtcNow;
        try { await Task.Run(() => _reviewStore.Save(result, decision, updated)); }
        catch (Exception error) { ReportReviewStorageError(error); }
    }

    private void ReportReviewStorageError(Exception error)
    {
        AppDiagnostics.Record("NSFW review storage", error);
        if (_closed || _reviewStorageErrorShown) return;
        _reviewStorageErrorShown = true;
        _ = Ui.Message(this, "审核记录保存失败", error.Message);
    }

    private sealed record ReviewChoice(NsfwReviewDecision Value, string Label)
    {
        public override string ToString() => Localization.Text(Label);
    }

    private static ComboBox ReviewControl(MediaFileEntry entry)
    {
        var control = new ComboBox
        {
            ItemsSource = new ReviewChoice[]
            {
                new(NsfwReviewDecision.Unreviewed, "未审核"), new(NsfwReviewDecision.Nsfw, "NSFW"),
                new(NsfwReviewDecision.NonNsfw, "非 NSFW"), new(NsfwReviewDecision.Uncertain, "不确定")
            },
            SelectedValueBinding = new Binding(nameof(ReviewChoice.Value)),
            DataContext = entry, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        control.Bind(ComboBox.SelectedValueProperty, new Binding(nameof(MediaFileEntry.ReviewDecision)) { Mode = BindingMode.TwoWay });
        control.Bind(ComboBox.IsEnabledProperty, new Binding(nameof(MediaFileEntry.HasTagResult)));
        ToolTip.SetTip(control, Localization.Text("人工审核结论；模型结果不会自动写入此项。"));
        return control;
    }

    private static void ClearReview(MediaFileEntry entry)
    {
        entry.HasTagResult = false; entry.ReviewDecision = NsfwReviewDecision.Unreviewed; entry.ReviewLoaded = false;
    }

    private static string NsfwStateText(NsfwSignalState state) => Localization.Text(state switch
    {
        NsfwSignalState.Suspected => "疑似 NSFW · 待审核",
        NsfwSignalState.ContextOnly => "仅命中提示标签",
        _ => "未检出风险标签"
    });

    private async Task ExportFeedbackAsync()
    {
        if (_busy) return;
        var reviewed = _entries.Where(entry => entry.ReviewDecision != NsfwReviewDecision.Unreviewed && entry.ReviewedAtUtc is not null
            && _results.ContainsKey(entry.Path)).Select(entry => new
            { Result = _results[entry.Path], Review = entry.ReviewDecision, ReviewedAt = entry.ReviewedAtUtc!.Value }).ToArray();
        if (reviewed.Length == 0) { await Ui.Message(this, "导出审核反馈", "请先分析并完成人工审核。"); return; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation; SetBusy(true);
        try
        {
            var threshold = Number(_threshold);
            var file = await StorageProvider.SaveFilePickerAsync(new()
            { Title = Localization.Text("导出审核反馈"), SuggestedFileName = "nsfw-feedback.jsonl", DefaultExtension = "jsonl" });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            for (var index = 0; index < reviewed.Length; index++)
            {
                var item = reviewed[index];
                if (!_closed) _status.Text = Localization.Format($"正在导出审核反馈 {index + 1} / {reviewed.Length}");
                await NsfwModeration.WriteFeedbackAsync(stream, item.Result, item.Review, item.ReviewedAt, threshold, operation.Token);
            }
            await stream.FlushAsync(operation.Token);
            if (!_closed) _status.Text = Localization.Format($"已导出 {reviewed.Length} 条审核反馈");
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("已停止"); }
        catch (Exception error) { if (!_closed) await Ui.Message(this, "导出失败", error.Message); }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }
}
