using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private Panel? _menuHome;
    private readonly List<Action> _menuStateUpdates = [];
    internal IReadOnlyList<WindowMenuCommand> WindowMenuCommands { get; private set; } = [];
    internal Control? MenuEditTarget { get; private set; }

    private void InitializeToolbarMenu()
    {
        _menuHome = (Panel)ApplicationMenu.Parent!;
        var old = ApplicationMenu.Items.OfType<MenuItem>().ToArray();
        var sections = ((App)Application.Current!).CreateWindowMenuCommands(this).ToArray();
        // Keep ff28d81's headings and leading commands; append the existing desktop operations.
        var file = old[0].Items.Skip(2).Select(WindowMenuCommand.FromControl).ToList();
        file.Add(WindowMenuCommand.Separator);
        file.Add(WindowMenuCommand.FromItem(old[1]));
        file.Add(WindowMenuCommand.FromItem(old[4]));
        sections[0] = sections[0] with { Children = sections[0].Children!.Concat([WindowMenuCommand.Separator]).Concat(file).ToArray() };
        sections[1] = sections[1] with { Children = sections[1].Children!.Concat([WindowMenuCommand.Separator])
            .Concat(old[2].Items.Select(WindowMenuCommand.FromControl)).ToArray() };
        sections[2] = sections[2] with { Children = sections[2].Children!.Concat(
            [WindowMenuCommand.Separator, WindowMenuCommand.FromItem(old[3]), WindowMenuCommand.FromItem((MenuItem)old[5].Items[1]!)]).ToArray() };
        sections[3] = sections[3] with { Children = sections[3].Children!.Concat(
            [WindowMenuCommand.Separator, WindowMenuCommand.FromItem((MenuItem)old[6].Items[1]!)]).ToArray() };
        WindowMenuCommands = sections;
        ApplicationMenu.Items.Clear();
        // Release parents before reusing the named controls, whose state existing handlers update.
        foreach (var index in new[] { 0, 2, 5, 6 }) old[index].Items.Clear();
        foreach (var section in sections) ApplicationMenu.Items.Add(CreateToolbarItem(section));
        ApplicationMenu.AddHandler(PointerPressedEvent, (_, _) => RefreshToolbarMenu(), RoutingStrategies.Tunnel);
        ApplicationMenu.AddHandler(KeyDownEvent, (_, _) => RefreshToolbarMenu(), RoutingStrategies.Tunnel);
        ApplicationMenu.AddHandler(MenuItem.SubmenuOpenedEvent, (_, _) => RefreshToolbarMenu());
        AddHandler(GotFocusEvent, (_, args) =>
        {
            if (args.Source is Control control && control is not MenuItem and not Menu && !control.GetVisualAncestors().Any(ancestor => ancestor is MenuItem or Menu))
                MenuEditTarget = control;
        });
        PropertyChanged += (_, args) =>
        {
            if (args.Property == NativeMenu.IsNativeMenuExportedProperty) ApplyToolbarMenu(ModernChrome);
        };
        Localization.Changed += ToolbarMenuLanguageChanged;
        Closed += (_, _) => Localization.Changed -= ToolbarMenuLanguageChanged;
        RefreshToolbarMenu();
    }

    private Control CreateToolbarItem(WindowMenuCommand command)
    {
        if (command.Header.Length == 0) return new Separator();
        var item = command.ExistingItem ?? new MenuItem { Header = command.Header };
        if (command.ExistingItem is null)
        {
            item.ToggleType = command.Checked is null ? MenuItemToggleType.None : MenuItemToggleType.CheckBox;
            if (command.Children is { } children)
                foreach (var child in children) item.Items.Add(CreateToolbarItem(child));
            else item.Click += (_, _) => { if (command.Enabled?.Invoke() != false) command.Execute?.Invoke(); };
            _menuStateUpdates.Add(() =>
            {
                item.IsEnabled = command.Enabled?.Invoke() ?? true;
                item.IsChecked = command.Checked?.Invoke() ?? false;
            });
        }
        if (command.Header == "功能介绍")
        {
            item.HotKey = new KeyGesture(Key.F1);
            ToolTip.SetTip(item, Localization.Text(command.Header) + " (F1)");
        }
        return item;
    }

    private void ToolbarMenuLanguageChanged(object? sender, EventArgs args) => RefreshToolbarMenu();
    private void RefreshToolbarMenu()
    {
        foreach (var update in _menuStateUpdates) update();
    }

    private void ApplyToolbarMenu(bool modern)
    {
        (ApplicationMenu.Parent as Panel)?.Children.Remove(ApplicationMenu);
        var host = modern ? ModernTitleBar : _menuHome!;
        host.Children.Add(ApplicationMenu);
        if (modern) Grid.SetRow(ApplicationMenu, 0);
        else ApplicationMenu.Bind(Grid.RowProperty, new DynamicResourceExtension("UiMainMenuRow"));
        Grid.SetColumn(ApplicationMenu, 0);
        ApplicationMenu.Bind(ThemeProperty, new DynamicResourceExtension(modern ? "MbMenuTheme" : "UiMenuTheme"));
        ApplicationMenu.Bind(HeightProperty, new DynamicResourceExtension(modern ? "MbToolbarHeight" : "UiMenuBarHeight"));
        ApplicationMenu.IsVisible = !OperatingSystem.IsMacOS() || !NativeMenu.GetIsNativeMenuExported(this);
        void ThemeChildren(ItemsControl parent, bool top)
        {
            foreach (var child in parent.Items)
            {
                if (child is MenuItem item)
                {
                    if (modern) item.Bind(ThemeProperty, new DynamicResourceExtension(top ? "MbTopLevelTheme" : "MbItemTheme"));
                    else item.Bind(ThemeProperty, new DynamicResourceExtension(top
                        ? ActualThemeVariant == Skin.MacOS9 ? "PlatinumTopLevelMenuItemTheme" : "XPTopLevelMenuItemTheme"
                        : "UiMenuItemTheme"));
                    ThemeChildren(item, false);
                }
                else if (child is Separator separator)
                {
                    if (modern) separator.Bind(ThemeProperty, new DynamicResourceExtension("MbSeparatorTheme"));
                    else separator.Bind(ThemeProperty, new DynamicResourceExtension(ActualThemeVariant == Skin.MacOS9
                        ? "PlatinumMenuSeparatorTheme" : "XPMenuSeparatorTheme"));
                }
            }
        }
        ThemeChildren(ApplicationMenu, true);
    }

}
