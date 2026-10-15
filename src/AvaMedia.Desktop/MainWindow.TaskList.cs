using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    // Task list items: section headers (进行中 / 排队 / 已完成) followed by their jobs (UISPEC 7.4).
    private readonly ObservableCollection<object> _visibleJobs = [];
    private readonly Dictionary<TaskSectionKind, TaskSection> _taskSections = Enum.GetValues<TaskSectionKind>().ToDictionary(kind => kind, kind => new TaskSection(kind));
    private bool _refreshingTaskList, _taskListContainersHooked;

    private int VisibleJobCount => _visibleJobs.Count(item => item is Job);

    private static bool IsFinished(Job job) => TaskSection.KindOf(job) == TaskSectionKind.Finished;

    private void RefreshTaskList()
    {
        if (_refreshingTaskList) return;
        _refreshingTaskList = true;
        try
        {
            HookTaskListContainers();
            // Grouping only changes the display; the persisted queue and its execution order stay as they are.
            var collapsed = _settings.CollapseCompletedTasks;
            List<object> visible = [];
            foreach (var kind in Enum.GetValues<TaskSectionKind>())
            {
                var jobs = _jobs.Where(job => TaskSection.KindOf(job) == kind).ToArray();
                if (jobs.Length == 0) continue;
                var section = _taskSections[kind];
                section.Update(jobs.Length, kind == TaskSectionKind.Finished ? jobs.Count(job => job.State == JobState.Failed) : 0,
                    kind == TaskSectionKind.Finished && collapsed);
                visible.Add(section);
                if (!section.Collapsed) visible.AddRange(jobs);
            }
            var included = visible.ToHashSet();
            foreach (var job in JobList.SelectedItems?.OfType<Job>().Where(job => !included.Contains(job)).ToArray() ?? [])
                JobList.SelectedItems?.Remove(job);
            for (var index = _visibleJobs.Count - 1; index >= 0; index--)
                if (!included.Contains(_visibleJobs[index])) _visibleJobs.RemoveAt(index);
            for (var index = 0; index < visible.Count; index++)
            {
                if (index < _visibleJobs.Count && ReferenceEquals(_visibleJobs[index], visible[index])) continue;
                var previous = _visibleJobs.IndexOf(visible[index]);
                if (previous < 0) _visibleJobs.Insert(index, visible[index]);
                else _visibleJobs.Move(previous, index);
            }
            foreach (var container in JobList.GetRealizedContainers()) MarkTaskContainer(container);
            QueueEmpty.IsVisible = _jobs.Count == 0;
            QueueEmpty.Text = Localization.Text("暂无任务，请从左侧选择工具");
            CollapseCompletedTasksViewMenu.IsChecked = collapsed;
            RefreshTaskSummary();
        }
        finally { _refreshingTaskList = false; }
    }

    private void HookTaskListContainers()
    {
        if (_taskListContainersHooked) return;
        _taskListContainersHooked = true;
        JobList.ContainerPrepared += (_, e) => MarkTaskContainer(e.Container);
        JobList.ContainerIndexChanged += (_, e) => MarkTaskContainer(e.Container);
    }

    /// <summary>Header containers never highlight; the last row of a section drops its hairline before the next header.</summary>
    private void MarkTaskContainer(Control container)
    {
        var index = JobList.IndexFromContainer(container);
        if (index < 0 || index >= _visibleJobs.Count) return;
        var item = _visibleJobs[index];
        container.Classes.Set("task-section", item is TaskSection);
        container.Classes.Set("section-end", item is Job && (index + 1 >= _visibleJobs.Count || _visibleJobs[index + 1] is TaskSection));
    }

    internal void ToggleCompletedTasks()
    {
        _settings.CollapseCompletedTasks = !_settings.CollapseCompletedTasks;
        _storage.SaveSettings(_settings);
        Refresh();
    }

    private void ToggleCompletedTasksClick(object? sender, RoutedEventArgs args) => ToggleCompletedTasks();

    /// <summary>Section headers are display-only and drop out of any selection (select all, keyboard).</summary>
    private bool DropSectionSelection()
    {
        if (JobList.SelectedItems is not { } selected) return false;
        var sections = selected.OfType<TaskSection>().ToArray();
        foreach (var section in sections) selected.Remove(section);
        return sections.Length > 0;
    }

    private void SelectTaskRows(IEnumerable<Job> jobs)
    {
        var selected = jobs.Where(_jobs.Contains).Distinct().ToArray();
        // Result notifications and tool windows must be able to reveal a folded task.
        if (selected.Any(IsFinished) && _settings.CollapseCompletedTasks)
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
