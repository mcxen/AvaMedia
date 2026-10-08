using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    internal static MainWindow CreateSetupPreview(string root, string theme)
    {
        var storage = new Storage(root);
        storage.SaveSettings(new()
        {
            Theme = theme, Language = "zh-CN", OutputFolder = Path.Combine(root, "output"),
            CloseToTray = false, CheckForUpdates = false, AutoDownloadRepairModel = false, ReduceMotion = true
        });
        var window = new MainWindow(storage) { Width = 1440, Height = 860, ShowInTaskbar = false };
        if (theme is "Light" or "Dark") window.SystemDecorations = SystemDecorations.None;
        foreach (var (name, feature, state, progress) in new[]
        {
            ("示例视频.mp4", "mp4", JobState.Running, 46d),
            ("示例音频.wav", "audio-mp3", JobState.Completed, 100d),
            ("示例图片.png", "image-webp", JobState.Waiting, 0d)
        })
        {
            window._jobs.Add(new()
            {
                Inputs = [Path.Combine(root, name)], FeatureId = feature,
                Output = Path.Combine(root, "output", Path.GetFileNameWithoutExtension(name) + "." + Catalog.Find(feature).Format),
                Options = new() { Format = Catalog.Find(feature).Format }, State = state, Progress = progress
            });
        }
        window.JobList.SelectedIndex = 1;
        window.Refresh();
        return window;
    }

    internal void PrepareSetupPreviewForRender()
    {
        OutputPath.Text = "输出文件夹 · AvaMedia";
        ElapsedText.Text = "耗时: 00:00:12";
    }

    internal void CloseSetupPreview()
    {
        _exitFinished = true; _closing = true; _timer.Stop();
        Close();
    }
}
