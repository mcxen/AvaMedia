using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

internal partial class MultiStageTaskWindow
{
    internal void ConfigurePreparation(Job job, AppSettings settings, Func<bool> canEdit, Action save)
    {
        var fields = new EntryParameters(job, this, settings, () => { if (canEdit()) save(); });
        var expander = new Expander { Header = Localization.Text("处理参数"), IsExpanded = true, Classes = { "ac" },
            Content = new ScrollViewer { MaxHeight = 180, Content = MsUi.Card(fields),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        void Update()
        {
            fields.IsEnabled = canEdit();
            SetPreparation(expander, canEdit());
        }
        ContentRefreshed += (_, _) => Update();
        ViewedPageChanged += (_, _) => Update();
        Update();
    }
}
