using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using System.Runtime.CompilerServices;

namespace AvaMedia.Desktop.Controls;

/// <summary>Release popup item containers before a live menu template replacement.</summary>
public sealed class MenuTemplateLifetime : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<MenuTemplateLifetime, ItemsControl, bool>("IsEnabled");
    private static readonly FuncTemplate<Panel?> ReleasedPanel = new(() => null);
    private static readonly ConditionalWeakTable<ItemsControl, PresenterState> Presenters = new();
    public static bool GetIsEnabled(ItemsControl control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ItemsControl control, bool value) => control.SetValue(IsEnabledProperty, value);

    static MenuTemplateLifetime()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ItemsControl>((control, args) =>
        {
            if (!GetIsEnabled(control)) return;
            var presenter = args.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter");
            var state = Presenters.GetOrCreateValue(control);
            if (state.Presenter is { } previous && !ReferenceEquals(previous, presenter))
                previous.SetValue(ItemsPresenter.ItemsPanelProperty, ReleasedPanel);
            state.Presenter = presenter;
        });
    }

    private sealed class PresenterState
    {
        // Popup children outlive the old template's visual tree. Retire their
        // generator only when a fresh presenter actually replaces it; resource
        // changes can temporarily unset Template while retaining the same tree.
        public ItemsPresenter? Presenter;
    }
}
