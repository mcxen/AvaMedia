using System.ComponentModel;
using Avalonia.Interactivity;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private string[] SelectedPlayableOutputs()
    {
        var selected = JobList.SelectedItems?.OfType<Job>().ToHashSet() ?? [];
        return _jobs.Where(selected.Contains)
            .Where(job => job.State == JobState.Completed && File.Exists(job.Output)
                && VideoFolderScanner.IsVideoFile(job.Output))
            .Select(job => job.Output).Distinct(VideoFolderScanner.PathComparer).ToArray();
    }

    private void JobContextMenuOpening(object? sender, CancelEventArgs args)
    {
        PlayOutputMenu.IsEnabled = SelectedPlayableOutputs().Length > 0;
        UpdateTaskEditingActions();
    }

    private async void PlayOutputSelectedClick(object? sender, RoutedEventArgs args)
    {
        // Recheck the outputs at click time: files can be moved after the menu opens.
        var outputs = SelectedPlayableOutputs();
        if (outputs.Length == 0)
        {
            await Ui.Message(this, "无法播放输出视频", "请选择已完成且输出视频仍存在的任务。");
            return;
        }
        try { new PlayerWindow(Engine, outputs).ShowForPlayback(this); }
        catch (Exception exception) { await Ui.Message(this, "播放器打开失败", exception.Message); }
    }
}
