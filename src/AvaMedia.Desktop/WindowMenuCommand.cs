using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

/// <summary>One command tree for native and in-window menus, including live enabled/check state.</summary>
internal sealed record WindowMenuCommand(string Header, Action? Execute = null,
    Func<bool>? Enabled = null, Func<bool>? Checked = null,
    IReadOnlyList<WindowMenuCommand>? Children = null, MenuItem? ExistingItem = null, Func<string>? HeaderText = null)
{
    public static WindowMenuCommand Separator { get; } = new("");

    public static WindowMenuCommand FromItem(MenuItem item) => new(
        item.Header?.ToString() ?? "", () => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)),
        () => item.IsEnabled, item.ToggleType == MenuItemToggleType.None ? null : () => item.IsChecked,
        item.Items.Count == 0 ? null : item.Items.Select(FromControl).ToArray(), item);

    public static WindowMenuCommand FromControl(object? control) => control is MenuItem item ? FromItem(item) : Separator;
}
