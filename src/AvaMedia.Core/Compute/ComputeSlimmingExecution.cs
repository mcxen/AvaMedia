using System.Diagnostics;

namespace AvaMedia.Core.Compute;

/// <summary>The current slimming analysis is calibrated to software CRF; changing encoder invalidates its quality evidence.</summary>
public static class ComputeSlimmingExecution
{
    public static async Task ExecuteAsync(MediaEngine engine, Job job, Action<double> progress, CancellationToken ct)
    {
        var log = new List<string>();
        var preference = ComputeSettings.Load(engine.Settings.AutoDetectGpu).Preference;
        var route = new ComputeRouter(ComputeEnvironment.Platform).Plan(new(job.FeatureId, ComputeCapability.VideoEncode,
            job.Options.VideoSlimming?.Codec ?? "h264", preference), log.Add);
        while (route.Chosen != ComputeBackend.Cpu)
            route.ReportFailure(route.Chosen, "当前视频瘦身的采样质量分析以 libx264/libx265 CRF 校准，尚未支持硬件编码器重新校准。", persist: false);
        var model = new ThroughputModel();
        ComputeProgressScope? computeProgress = null; ThroughputKey? key = null; Stopwatch? watch = null;
        try
        {
            await new VideoSlimming(engine).ExecuteAsync(job, value =>
            {
                progress(value);
                if (value is < 35 or >= 99 || job.Options.VideoSlimming?.Analysis is not { Worthwhile: true } analysis) return;
                if (computeProgress is null)
                {
                    key = new(job.FeatureId, ComputeBackend.Cpu, ThroughputModel.ResolutionBand(analysis.OutputWidth, analysis.OutputHeight), analysis.Encoder);
                    watch = Stopwatch.StartNew(); computeProgress = new(job, model, key, job.Duration, () => watch.Elapsed);
                }
                computeProgress.Update(Math.Clamp((value - 35) / 63 * 100, 0, 99.9));
            }, ct).ConfigureAwait(false);
            if (watch is not null && key is not null && job.CompletionKind != JobCompletionKind.Unchanged)
                model.Observe(key, job.Duration, watch.Elapsed);
            route.ReportSuccess(job.Options.VideoSlimming?.Analysis?.Encoder ?? "未执行编码");
        }
        finally
        {
            computeProgress?.Dispose();
            foreach (var line in log) job.AppendLog(line);
            job.SetComputeOutcome(route.Outcome(job.Options.VideoSlimming?.Analysis?.Encoder ?? "软件质量分析"));
        }
    }
}
