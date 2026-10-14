using AvaMedia.Core;
using AvaMedia.Mcp;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private sealed partial class DesktopMcpWorkspace
    {
        public Task<AppSettings> SettingsAsync(Action<AppSettings>? change, string? expectedRevision, CancellationToken ct) => Ui(() =>
        {
            if (change is null) return owner._settings.Clone();
            if (expectedRevision is not null && expectedRevision != McpConfiguration.Revision(owner._settings))
                throw new InvalidOperationException("设置已改变，请重新读取设置后再提交。");
            var prior = owner._settings.Clone(); var draft = prior.Clone(); change(draft);
            SettingsPolicy.Validate(draft);
            if (draft.Theme is not ("System" or "Light" or "Dark" or "MacOS9" or "WindowsXP")
                || draft.Language is not ("system" or "zh-CN" or "en-US")
                || draft.AccentColor is not ("Blue" or "Purple" or "Pink" or "Red" or "Orange" or "Yellow" or "Green" or "Gray"))
                throw new ArgumentException("主题、语言或强调色无效。");
            if (!LinuxInterfaceScaling.Percentages.Contains(draft.LinuxInterfaceScalePercent))
                throw new ArgumentException("界面缩放须为 0、100、125、150 或 200。");
            if (!Enum.TryParse<ModelSourceKind>(draft.ModelSource, true, out var source) || !Enum.IsDefined(source))
                throw new ArgumentException("模型下载源无效。");
            draft.OutputFolder = new McpFiles().Check(draft.OutputFolder);
            if (MediaEngine.UsesBundledTools && (draft.FFmpegPath != prior.FFmpegPath || draft.FFprobePath != prior.FFprobePath || draft.YtDlpPath != prior.YtDlpPath))
                throw new ArgumentException("当前使用内置工具，不能修改外部工具路径。");
            var removed = prior.OnlineAi.Providers.Where(p => !draft.OnlineAi.Providers.Any(next => next.Id == p.Id)).Select(p => p.Id).ToHashSet();
            var fallback = prior.OnlineAi.Providers.FirstOrDefault(p => p.Enabled
                && (prior.OnlineAi.DefaultProviderId.Length == 0 || p.Id == prior.OnlineAi.DefaultProviderId))?.Id ?? "";
            bool Removed(string? id) => removed.Contains(string.IsNullOrEmpty(id) ? fallback : id);
            foreach (var job in owner._jobs.Where(job => job.State is JobState.Waiting or JobState.Running or JobState.Paused or JobState.Stopping))
            {
                var o = job.Options; var summary = o.VideoSummary;
                var caption = o.MediaTag?.Analysis ?? o.FolderClassification?.Analysis;
                if (o.VlRename is { Mode: "online" } vl && Removed(vl.ProviderId)
                    || caption is { GenerateCaptions: true, CaptionLocalModelId: null } && Removed(caption.CaptionProviderId)
                    || summary?.Provider == VideoSummaryProvider.Online && summary.NeedsAi
                        && (Removed(summary.SummaryProviderId.Length > 0 ? summary.SummaryProviderId : summary.OnlineProviderId)
                            || summary.AnalyzeFrames && Removed(summary.VisionProviderId.Length > 0 ? summary.VisionProviderId : summary.OnlineProviderId)))
                    throw new InvalidOperationException("供应商仍被未结束任务引用，请先结束任务。");
            }
            try { owner._settings.CopyFrom(draft); owner.ApplyOptions(); }
            catch { owner._settings.CopyFrom(prior); throw; }
            return owner._settings.Clone();
        }, ct);
    }
}
