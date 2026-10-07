using AvaMedia.Core;

namespace AvaMedia.Desktop;
public partial class MainWindow
{
    private async Task ConfigureBatchToolsAsync(string[]? files, bool screenshots)
    {
        // Source file names must stay stable while FFmpeg has running queue jobs.
        if (_queue.IsRunning)
        {
            await Ui.Message(this, "批量工具", "请在当前转换任务完成或停止后打开批量工具，以便同步重命名后的源文件路径。");
            return;
        }
        var window = new BatchToolsWindow(Engine, _settings.OutputFolder, files, screenshots: screenshots);
        window.Renamed += mappings =>
        {
            var map = mappings.ToDictionary(i => i.Source, i => i.Target, BatchVideoTools.PathComparer);
            foreach (var job in _jobs)
                job.Inputs = job.Inputs.Select(path => map.GetValueOrDefault(path) ?? path).ToArray();
            JobList.ItemsSource = null;
            JobList.ItemsSource = _jobs;
            Save();
            Refresh();
        };
        await window.ShowDialog(this);
    }
}
