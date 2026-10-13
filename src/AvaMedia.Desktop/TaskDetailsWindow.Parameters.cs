using System.Globalization;
using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class TaskDetailsWindow
{
    private string _sizeKey = "";
    private long? _inputBytes;
    private long? _outputBytes;

    private void AddSlimmingResult()
    {
        _ = LoadFileSizesAsync();
        var analysis = _job.Options.VideoSlimming?.Analysis;
        var sourceBytes = analysis?.SourceBytes ?? _inputBytes;
        Row(_result, "原体积", sourceBytes is { } original ? Size(original) : null);
        if (analysis?.ActualBytes is { } encodedBytes)
        {
            Row(_result, Unchanged ? "编码结果体积（未保留）" : "实际产出体积", Size(encodedBytes));
            Row(_result, "实际节省", analysis.ActualSaving is { } actualSaving ? actualSaving.ToString("0.#", CultureInfo.InvariantCulture) + "%" : null);
        }
        else if (!Unchanged && _job.State == JobState.Completed && !_job.HasInternalOutput)
        {
            Row(_result, "实际产出体积", _outputBytes is { } output ? Size(output) : null);
            if (sourceBytes is > 0 && _outputBytes is { } actual)
                Row(_result, "实际节省", ((1 - actual / (double)sourceBytes.Value) * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%");
        }
        if (analysis is null) return;
        _result.Children.Add(Ui.ComponentText("瘦身分析", "heading"));
        Row(_result, "分析结论", Localization.Text(analysis.Diagnosis));
        Row(_result, "预估体积（采样）", analysis.EstimatedBytes is { } estimated ? Size(estimated) : null);
        Row(_result, "预估节省", analysis.EstimatedSaving is { } saving ? saving.ToString("0.#", CultureInfo.InvariantCulture) + "%" : null);
        Row(_result, "编码器", analysis.Encoder);
        Row(_result, "画面", analysis.Quality is not null ? $"{analysis.Width} × {analysis.Height} → {analysis.OutputWidth} × {analysis.OutputHeight}" : $"{analysis.Width} × {analysis.Height}");
        Row(_result, "质量参数", analysis.Quality?.ToString(CultureInfo.InvariantCulture));
        Row(_result, "平均 SSIM", analysis.MeanSsim?.ToString("0.####", CultureInfo.InvariantCulture));
        Row(_result, "最低 SSIM", analysis.WorstSsim?.ToString("0.####", CultureInfo.InvariantCulture));
        Row(_result, "最低 XPSNR", analysis.WorstXpsnr is { } xpsnr ? xpsnr.ToString("0.##", CultureInfo.InvariantCulture) + " dB" : null);
        Row(_result, "采样", Localization.Format($"{analysis.Samples} 段 · 合计 {analysis.SampleSeconds:0.##} 秒"));
        Row(_result, "保留轨道", Localization.Format($"{analysis.AudioTracks} 音轨 · {analysis.SubtitleTracks} 字幕轨"));
    }

    // Read byte counts once per source/output change without media decoding or recursive scans.
    private async Task LoadFileSizesAsync()
    {
        var output = HasOutput ? _job.UserOutput : "";
        var inputs = _job.Inputs.ToArray();
        var key = string.Join('\0', inputs.Append(output));
        if (key == _sizeKey) return;
        _sizeKey = key; _inputBytes = null; _outputBytes = null;
        try
        {
            var sizes = await Task.Run(() =>
            {
                long? total = inputs.Length == 0 ? null : 0;
                foreach (var path in inputs)
                {
                    var size = FileBytes(path);
                    if (size is null || total is null) total = null;
                    else total += size.Value;
                }
                return (Input: total, Output: FileBytes(output));
            }, _lifetime.Token);
            if (_closed || key != _sizeKey) return;
            _inputBytes = sizes.Input; _outputBytes = sizes.Output; Refresh();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { AppDiagnostics.Record("Read task file sizes", error); }
    }
    private static long? FileBytes(string path)
    {
        try { var file = new FileInfo(path); return file.Exists ? file.Length : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private void AddParameters()
    {
        var options = _job.Options;
        _parameters.Children.Add(Ui.ComponentText("任务参数", "heading"));
        using var summary = new JobRowDetails(_job);
        Row(_parameters, "工具", Localization.Text(FeatureNavigation.Label(Catalog.Find(_job.FeatureId))));
        Row(_parameters, "处理设置", summary.SettingsSummary);
        Row(_parameters, "输出格式", options.Format.ToUpperInvariant());
        if (options.VideoSlimming is { } slim)
        {
            Row(_parameters, "画质档位", Localization.Text(slim.Preset switch
            { VideoSlimmingPreset.Preserve => "保真", VideoSlimmingPreset.Smaller => "更小", _ => "均衡" }));
            Row(_parameters, "视频编码", slim.Codec == "hevc" ? "HEVC (H.265)" : "H.264");
            Row(_parameters, "音频", Localization.Text("保留全部音轨"));
            return;
        }
        if (options.VideoCompression is { } compression)
        {
            var effective = VideoCompression.Effective(compression);
            Row(_parameters, "视频编码", effective.Codec == "hevc" ? "HEVC (H.265)" : "H.264");
            Row(_parameters, "编码速度", Localization.Text(effective.Speed switch
            { VideoEncodingSpeed.Fast => "快速", VideoEncodingSpeed.Slow => "慢速", _ => "均衡" }));
            Row(_parameters, "分辨率上限", effective.MaxDimension > 0 ? effective.MaxDimension + " px" : Localization.Text("保持原始"));
            Row(_parameters, "帧率上限", effective.MaxFrameRate > 0 ? effective.MaxFrameRate + " fps" : Localization.Text("保持原始"));
            Row(_parameters, "音频", effective.KeepAudio ? effective.AudioBitrate + " kbps" : Localization.Text("静音"));
            Row(_parameters, "优先使用 GPU", Localization.Text(effective.PreferGpu ? "是" : "否"));
            return;
        }
        var feature = Catalog.Find(_job.FeatureId);
        if (feature.Category is "视频" or "音频" && options.ImageCompression is null
            && options.Transcription is null && options.VideoSummary is null && options.PersonClip is null)
        {
            if (feature.Category == "视频") Row(_parameters, "视频编码", Localization.Text(options.VideoCodec));
            Row(_parameters, "音频编码", options.Mute ? Localization.Text("静音") : Localization.Text(options.AudioCodec));
            Row(_parameters, "流处理", Localization.Text(options.CopyStreams ? "直接复制流" : options.PreserveSourceAttributes ? "原视频编码 · 其他轨道直拷" : "重新编码"));
            if (!options.CopyStreams && !options.PreserveSourceAttributes && !MediaEngine.IsAudio(options.Format) && !MediaEngine.IsImage(options.Format))
                Row(_parameters, "码率模式", options.VideoRateMode switch
                {
                    VideoRateMode.Bitrate => options.VideoBitrate + " kbps",
                    VideoRateMode.Source => Localization.Text("参考源码率"),
                    _ => Localization.Format($"质量 {options.Quality}")
                });
            if (options.Width > 0 || options.Height > 0) Row(_parameters, "画面", $"{options.Width} × {options.Height}");
            if (options.Fps > 0) Row(_parameters, "帧率", options.Fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
            if (options.Start > 0 || options.End > 0) Row(_parameters, "截取范围", MediaTime.Format(options.Start) + " → " + (options.End > 0 ? MediaTime.Format(options.End) : Localization.Text("结尾")));
            if (options.Speed != 1) Row(_parameters, "播放速度", options.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×");
            if (options.Volume != 1) Row(_parameters, "音量", options.Volume.ToString("0.##", CultureInfo.InvariantCulture) + "×");
            if (options.CropWidth > 0) Row(_parameters, "裁剪", $"{options.CropX}, {options.CropY} · {options.CropWidth} × {options.CropHeight}");
            if (options.Rotation != 0) Row(_parameters, "旋转", options.Rotation + "°");
            if (options.Subtitle.Length > 0) Row(_parameters, "字幕", options.Subtitle);
        }
        if (_job.InputOptions is not { Count: > 0 } fileOptions) return;
        for (var index = 0; index < fileOptions.Count; index++)
        {
            var item = new Job { FeatureId = _job.FeatureId, Options = fileOptions[index] };
            using var fileSummary = new JobRowDetails(item);
            Row(_parameters, index < _job.Inputs.Length ? Path.GetFileName(_job.Inputs[index]) : Localization.Format($"文件 {index + 1}"), fileSummary.SettingsSummary);
        }
    }
}
