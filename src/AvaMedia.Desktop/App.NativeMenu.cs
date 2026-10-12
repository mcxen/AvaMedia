using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop;

public sealed partial class App
{
    private IClassicDesktopStyleApplicationLifetime? DesktopLifetime => ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
    private Window? NativeMenuWindow => DesktopLifetime?.Windows.FirstOrDefault(window => window.IsActive)
        ?? (DesktopLifetime?.MainWindow is { IsVisible: true } main ? main : null);
    private MainWindow? NativeTaskWindow => DesktopLifetime?.Windows.OfType<MainWindow>().FirstOrDefault();

    private void InitializeNativeMenu()
    {
        // App's NativeMenu is the application submenu, not the full window menu bar.
        // Set it before the native exporter starts so it never creates About Avalonia.
        var application = new NativeMenu();
        AddNativeItem(application, "关于 AvaMedia", () => _ = RunNativeCommandAsync(() => Ui.AboutAsync(NativeMenuWindow)));
        AddNativeItem(application, "设置…", () => _ = RunNativeCommandAsync(() =>
            NativeTaskWindow!.ShowSettingsAsync(NativeMenuWindow!)),
            () => NativeTaskWindow is { CanUseNativeMenu: true } && NativeMenuWindow is { IsEnabled: true });
        application.Add(new NativeMenuItemSeparator());
        AddNativeItem(application, "隐藏 AvaMedia", HideNativeApplication);
        application.Add(new NativeMenuItemSeparator());
        AddNativeItem(application, "退出 AvaMedia", RequestNativeExit);
        // A named parent prevents the exporter from creating an unnamed app menu holder.
        _ = new NativeMenu { new NativeMenuItem("AvaMedia") { Menu = application } };
        NativeMenu.SetMenu(this, application);

        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (DesktopLifetime?.Args?.Any(argument => argument is "--mcp-background" or "--server") == true) return;
            // Each window retains the same headings when dialogs or editors gain focus.
            if (NativeMenu.GetMenu(window) is not null) return;
            NativeMenu.SetMenu(window, CreateNativeWindowMenu(window));
            window.AddHandler(InputElement.KeyDownEvent, NativeApplicationKeyDown, RoutingStrategies.Tunnel);
        });
    }

    private void RequestNativeExit()
    {
        if (NativeTaskWindow is { } main) main.RequestExit();
        else DesktopLifetime?.TryShutdown();
    }

    private void NativeApplicationKeyDown(object? sender, KeyEventArgs args)
    {
        // Keep the system hide/quit shortcuts without showing shortcut hints in the menu.
        if (args.KeyModifiers != KeyModifiers.Meta) return;
        if (args.Key == Key.H) { args.Handled = true; HideNativeApplication(); }
        else if (args.Key == Key.Q) { args.Handled = true; RequestNativeExit(); }
    }

    private NativeMenu CreateNativeWindowMenu(Window window)
    {
        var menu = new NativeMenu();
        NativeMenu Section(string title)
        {
            var section = new NativeMenu();
            menu.Add(new NativeMenuItem(title) { Menu = section });
            return section;
        }
        bool CanCreateTask() => window is MainWindow { CanUseNativeMenu: true, IsEnabled: true };
        bool CanOpen() => CanCreateTask() || window is PlayerWindow { CanOpenFiles: true, IsEnabled: true };
        async Task OpenFiles(bool recent)
        {
            if (window is MainWindow main) await main.OpenFromNativeMenuAsync(recent);
            else if (window is PlayerWindow player)
            {
                var files = recent ? await ToolFilePicker.OpenRecentFilesAsync(window) : await Ui.Pick(window, "打开视频 / 音频");
                if (files.Length > 0) player.OpenFiles(files);
            }
        }
        var file = Section("文件");
        AddNativeItem(file, "新建任务", () => _ = RunNativeCommandAsync(() => ((MainWindow)window).NewFromNativeMenuAsync()), CanCreateTask);
        AddNativeItem(file, "打开…", () => _ = RunNativeCommandAsync(() => OpenFiles(false)), CanOpen);
        AddNativeItem(file, "最近使用…", () => _ = RunNativeCommandAsync(() => OpenFiles(true)), CanOpen);

        var edit = Section("编辑");
        foreach (var action in new[] { "撤销", "重做", "剪切", "复制", "粘贴", "全选" })
            AddNativeItem(edit, action, () => ExecuteNativeEdit(window, action), () => CanNativeEdit(window, action));

        var windows = Section("窗口");
        AddNativeItem(windows, "最小化", () => window.WindowState = WindowState.Minimized, () => window.CanMinimize);
        AddNativeItem(windows, "缩放", () =>
        {
            if (Skin.UsesCustomChrome(window.ActualThemeVariant)) Skin.Zoom(window);
            else Controls.WindowChrome.ToggleMaximize(window);
        }, () => window.CanResize && window.CanMaximize);
        var help = Section("帮助");
        AddNativeItem(help, "功能介绍", () =>
        {
            if (window is MainWindow main) main.ShowNativeHelp();
            else new HelpWindow().Show(window);
        }, () => window.IsEnabled);
        return menu;
    }

    private static void AddNativeItem(NativeMenu menu, string title, Action action, Func<bool>? enabled = null)
    {
        var item = new NativeMenuItem(title) { IsEnabled = enabled?.Invoke() ?? true };
        item.Click += (_, _) => { if (enabled?.Invoke() != false) action(); };
        if (enabled is not null) menu.NeedsUpdate += (_, _) => item.IsEnabled = enabled();
        menu.Add(item);
    }

    private async Task RunNativeCommandAsync(Func<Task> command)
    {
        try { await command(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppDiagnostics.Record("Native menu", error);
            if (NativeMenuWindow is { } owner) await Ui.Message(owner, "操作失败", error.Message);
        }
    }

    private static Control? NativeEditTarget(Window window)
    {
        if (window.FocusManager?.GetFocusedElement() is not Control focused) return null;
        return focused.GetVisualAncestors().OfType<Control>().Prepend(focused)
            .FirstOrDefault(control => control is TextBox or SelectableTextBlock);
    }

    private static bool CanNativeEdit(Window window, string action)
    {
        if (!window.IsEnabled) return false;
        if (NativeEditTarget(window) is TextBox text)
            return action switch
            {
                "撤销" => text.CanUndo && !text.IsReadOnly, "重做" => text.CanRedo && !text.IsReadOnly,
                "剪切" => text.CanCut, "复制" => text.CanCopy, "粘贴" => text.CanPaste,
                "全选" => !string.IsNullOrEmpty(text.Text), _ => false
            };
        if (NativeEditTarget(window) is SelectableTextBlock label)
            return action == "全选" ? !string.IsNullOrEmpty(label.Text) : action == "复制" && !string.IsNullOrEmpty(label.SelectedText);
        if (window is EditorWindow editor)
            return action switch { "撤销" => editor.CanNativeUndo, "重做" => editor.CanNativeRedo, _ => false };
        return false;
    }

    private static void ExecuteNativeEdit(Window window, string action)
    {
        if (!CanNativeEdit(window, action)) return;
        if (NativeEditTarget(window) is TextBox text)
        {
            switch (action)
            {
                case "撤销": text.Undo(); break;
                case "重做": text.Redo(); break;
                case "剪切": text.Cut(); break;
                case "复制": text.Copy(); break;
                case "粘贴": text.Paste(); break;
                case "全选": text.SelectAll(); break;
            }
        }
        else if (NativeEditTarget(window) is SelectableTextBlock label)
        {
            if (action == "复制") label.Copy(); else label.SelectAll();
        }
        else if (window is EditorWindow editor) editor.NativeUndoRedo(action == "重做");
    }

    private static void HideNativeApplication()
    {
        // Hide the NSApplication so all owned windows hide together and Dock reopening works.
        var application = SendObjectiveC(GetObjectiveCClass("NSApplication"), GetObjectiveCSelector("sharedApplication"));
        SendObjectiveCWithObject(application, GetObjectiveCSelector("hide:"), IntPtr.Zero);
    }
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetObjectiveCClass(string name);
    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr GetObjectiveCSelector(string name);
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendObjectiveC(IntPtr receiver, IntPtr selector);
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendObjectiveCWithObject(IntPtr receiver, IntPtr selector, IntPtr argument);
}
