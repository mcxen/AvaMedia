using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AvaMedia.Desktop;

internal static class ToolExecution
{
    private static readonly ConditionalWeakTable<Window, CheckBox> Choices = new();
    public static bool StartImmediately(Window owner) => Choices.TryGetValue(owner, out var choice) && choice.IsChecked != true;

    public static void Configure(Window owner, Button button, string action, bool editing = false)
    {
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
