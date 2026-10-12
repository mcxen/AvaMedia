using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class AiTaskWindow
{
    private PersonTaskView? _personView;

    private void BuildPersonProgress(IMediaEngine engine, Border footer)
    {
        Width = 1000; Height = 650; MinWidth = 900; MinHeight = 590;
        Classes.Add("person-task-window");
        Bind(BackgroundProperty, new DynamicResourceExtension("UiCanvas"));
        _personView = new PersonTaskView(engine, _job, _records, _error);
        // Actions retain the queue callbacks and the shared action error handling.
        if (_stop.Parent is Panel left) left.Children.Remove(_stop);
        if (_result.Parent is Panel right) right.Children.Insert(0, _stop);
        footer.Padding = new(0); footer.BorderThickness = new(0);
        var layout = new Grid { RowDefinitions = new("*,Auto"), Margin = new(20, 0, 20, 0), RowSpacing = 14 };
        layout.Children.Add(_personView);
        footer.Margin = new(0, 0, 0, 16);
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Content = WindowChrome.WrapDialog(this, layout);
    }
}
