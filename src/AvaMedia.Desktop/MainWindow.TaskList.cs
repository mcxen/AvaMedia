using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<Job> _visibleJobs = [];
    private static readonly Geometry CollapsedTaskChevron = Geometry.Parse("M 3,2 L 7,6 L 3,10");
    private static readonly Geometry ExpandedTaskChevron = Geometry.Parse("M 2,3 L 6,7 L 10,3");
    private bool _refreshingTaskList;

    private void RefreshTaskList()
    {
        if (_refreshingTaskList) return;
        _refreshingTaskList = true;
        try
        {
            // The visible list never changes the persisted queue or its execution order.
            var visible = _jobs.Where(job => !_settings.CollapseCompletedTasks || job.State != JobState.Completed).ToArray();
            var included = visible.ToHashSet();
            foreach (var job in JobList.SelectedItems?.OfType<Job>().Where(job => !included.Contains(job)).ToArray() ?? [])
                JobList.SelectedItems?.Remove(job);
            for (var index = _visibleJobs.Count - 1; index >= 0; index--)
                if (!included.Contains(_visibleJobs[index])) _visibleJobs.RemoveAt(index);
            for (var index = 0; index < visible.Length; index++)
            {
                if (index < _visibleJobs.Count && ReferenceEquals(_visibleJobs[index], visible[index])) continue;
                var previous = _visibleJobs.IndexOf(visible[index]);
                if (previous < 0) _visibleJobs.Insert(index, visible[index]);
                else _visibleJobs.Move(previous, index);
            }
            var completed = _jobs.Count(job => job.State == JobState.Completed);
            CompletedTasksBar.IsVisible = completed > 0;
            CompletedTasksChevron.Data = _settings.CollapseCompletedTasks ? CollapsedTaskChevron : ExpandedTaskChevron;
            Localization.SetText(CompletedTasksLabel, $"已完成（{completed}）");
            var action = Localization.Text(_settings.CollapseCompletedTasks ? "展开已完成任务" : "折叠已完成任务");
            ToolTip.SetTip(CompletedTasksToggle, action);
            AutomationProperties.SetName(CompletedTasksToggle, action);
            CollapseCompletedTasksViewMenu.IsChecked = _settings.CollapseCompletedTasks;
        }
        finally { _refreshingTaskList = false; }
    }

    private void ToggleCompletedTasksClick(object? sender, RoutedEventArgs args)
    {
        _settings.CollapseCompletedTasks = !_settings.CollapseCompletedTasks;
        _storage.SaveSettings(_settings);
        Refresh();
    }

    private void SelectTaskRows(IEnumerable<Job> jobs)
    {
        var selected = jobs.Where(_jobs.Contains).Distinct().ToArray();
        // Result notifications and tool windows must be able to reveal a folded task.
        if (selected.Any(job => job.State == JobState.Completed) && _settings.CollapseCompletedTasks)
        {
            _settings.CollapseCompletedTasks = false;
            _storage.SaveSettings(_settings);
        }
        RefreshTaskList();
        JobList.SelectedItems?.Clear();
        foreach (var job in selected) JobList.SelectedItems?.Add(job);
        if (selected.FirstOrDefault() is { } first) JobList.ScrollIntoView(first);
        Refresh();
    }
}
