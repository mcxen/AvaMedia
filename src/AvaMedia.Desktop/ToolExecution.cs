using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AvaMedia.Desktop;

internal static class ToolExecution
{
    private static readonly ConditionalWeakTable<Window, CheckBox> Choices = new();
    private sealed record Completion(Action<object?> Resolve);
    private static readonly ConditionalWeakTable<Window, Completion> Completions = new();
    private sealed class Draft(Func<Task> save)
    {
        public Func<Task> Save { get; } = save;
        public bool Saving, Finished;
        public object? Result;
    }
    private static readonly ConditionalWeakTable<Window, Draft> Drafts = new();

    public static void SaveOnClose(Window window, Func<Task> save)
    {
        var draft = new Draft(save); Drafts.Add(window, draft);
        window.Closing += (sender, args) =>
        {
            if (draft.Finished || !Completions.TryGetValue(window, out _)) return;
            args.Cancel = true;
            if (draft.Saving) return;
            draft.Saving = true; window.IsEnabled = false;
            _ = SaveAndCloseAsync();
        };
        async Task SaveAndCloseAsync()
        {
            try { await draft.Save(); }
            catch (Exception error) { AppDiagnostics.Record("AI task draft", error); await Ui.Message(window, "保存任务结果失败", error.Message); }
            draft.Finished = true;
            if (Completions.TryGetValue(window, out var completion)) completion.Resolve(draft.Result);
            window.Close(draft.Result);
        }
    }

    public static Task<T?> ShowAsync<T>(Window owner, Window window) where T : class
    {
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Completions.Add(window, new(value => completion.TrySetResult(value as T)));
        window.Closed += (_, _) => { Completions.Remove(window); completion.TrySetResult(null); };
        window.Show(owner);
        return completion.Task;
    }

    public static void Complete(Window window, object result)
    {
        if (Drafts.TryGetValue(window, out var draft) && !draft.Finished && Completions.TryGetValue(window, out _))
        { draft.Result = result; window.Close(result); return; }
        if (Completions.TryGetValue(window, out var completion)) completion.Resolve(result);
        window.Close(result);
    }
    public static bool StartImmediately(Window owner) => Choices.TryGetValue(owner, out var choice) && choice.IsChecked != true;

    public static void Configure(Window owner, Button button, string action, bool editing = false)
    {
        StableLayout.Reserve(button, action, "加入队列", "保存修改", "检查文件…");
        if (editing) return;
        var choice = new CheckBox { Content = "仅加入队列", VerticalAlignment = VerticalAlignment.Center };
        Choices.Add(owner, choice);
        void Refresh() => button.Content = Localization.Text(choice.IsChecked == true ? "加入队列" : action);
        choice.IsCheckedChanged += (_, _) => Refresh();
        button.PropertyChanged += (_, change) =>
        {
            if (change.Property == ContentControl.ContentProperty && Equals(button.Content, Localization.Text("加入队列")) && choice.IsChecked != true) Refresh();
        };
        owner.Opened += (_, _) =>
        {
            if (button.Parent is not Panel parent) return;
            var index = parent.Children.IndexOf(button);
            var wrapper = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
                HorizontalAlignment = button.HorizontalAlignment, VerticalAlignment = button.VerticalAlignment };
            Grid.SetColumn(wrapper, Grid.GetColumn(button)); Grid.SetRow(wrapper, Grid.GetRow(button));
            Grid.SetColumnSpan(wrapper, Grid.GetColumnSpan(button)); Grid.SetRowSpan(wrapper, Grid.GetRowSpan(button));
            parent.Children.Remove(button); wrapper.Children.Add(choice); wrapper.Children.Add(button); parent.Children.Insert(index, wrapper);
            button.PropertyChanged += (_, change) => { if (change.Property == Control.IsEnabledProperty) choice.IsEnabled = button.IsEnabled; };
            choice.IsEnabled = button.IsEnabled;
        };
        Refresh();
    }
}
