using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace AvaMedia.Controls;

/// <summary>沿用 ListBox 的键盘选择、焦点与无障碍，只改变分段外观。</summary>
public sealed class SegmentedControl : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);
    public SegmentedControl()
    {
        Classes.Add("ac-segmented");
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        SelectionMode = SelectionMode.Single;
        ContainerPrepared += (_, e) => { if (e.Container is ListBoxItem item) item.Classes.Set("no-divider", e.Index == 0 || e.Index == SelectedIndex + 1); };
        SelectionChanged += (_, _) =>
        {
            for (var i = 0; i < ItemCount; i++)
                if (ContainerFromIndex(i) is ListBoxItem item) item.Classes.Set("no-divider", i == 0 || i == SelectedIndex + 1);
        };
    }
}
