using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigurePersonClipAsync(string[]? files, Job? editing = null)
    {
        async Task ManageModels(Window owner, string? modelId)
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(modelId);
            settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }
        var folder = editing is null ? _settings.OutputFolder : Path.GetDirectoryName(editing.Output)!;
        var request = await new PersonClipWindow(Engine, _settings, files, ManageModels, folder,
            editing?.Options.PersonClip, editing is not null, editing is null ? null :
                string.Equals(folder, Path.GetDirectoryName(Path.GetFullPath(editing.Inputs[0])),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .ShowDialog<PersonClipRequest?>(this);
        if (request is null || !_settings.EnableBetaFeatures) return;
        try
        {
            var reserved = (editing is null ? _jobs.Select(job => job.Output) : EditingReservations(editing)).ToArray();
            var jobs = QuickClipWorkflow.PrepareJoinedJobs(request.Edits, request.Preset, new(), request.OutputFolder,
                request.OutputToSource, "People", reserved);
            if (editing is not null) ApplyEditedJobs(editing, jobs);
            else AddToolJobs(jobs, request.StartImmediately);
        }
        catch (Exception error) { await Ui.Message(this, "人物检测参数错误", error.Message); }
    }
}
