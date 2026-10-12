using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    internal bool CanUseNativeMenu => !_closing;
    internal Task NewFromNativeMenuAsync() => Configure(_last);
    internal async Task OpenFromNativeMenuAsync(bool recent)
    {
        var files = recent ? await ToolFilePicker.OpenRecentFilesAsync(this) : await Ui.Pick(this, "打开媒体文件");
        if (files.Length > 0) await Configure(_last, files);
    }
    internal void ShowNativeHelp() => HelpClick(this, new RoutedEventArgs());
}
