using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigureMediaAiAsync(string[]? files)
    {
        if (_queue.IsRunning)
        {
            await Ui.Message(this, "AI 标签", "请在当前转换任务完成或停止后打开，以便同步重命名后的源文件路径。");
            return;
        }
        var window = new MediaAiWindow(Engine, _settings, files, async owner =>
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        });
        window.Renamed += mappings =>
        {
            var map = mappings.ToDictionary(item => item.Source, item => item.Target, BatchRename.PathComparer);
            foreach (var job in _jobs) job.Inputs = job.Inputs.Select(path => map.GetValueOrDefault(path) ?? path).ToArray();
            JobList.ItemsSource = null; JobList.ItemsSource = _jobs; Save(); Refresh();
        };
        await window.ShowDialog(this);
    }
}
