using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static partial class Ui
{
    private const string ExclusiveKeyDigest = "7a2ec40ff8a1247c532309355f798a779e00acff579c63eec3636ffb2902c1ac";

    internal static void HookExclusiveToolsActivation(Control icon, Window owner, Action unlock)
    {
        var doubleClicks = 0;
        long lastDoubleClick = 0;
        var opening = false;
        icon.PointerPressed += async (_, args) =>
        {
            if (opening || !args.GetCurrentPoint(icon).Properties.IsLeftButtonPressed) return;
            var now = Environment.TickCount64;
            if (now - lastDoubleClick > 2000) doubleClicks = 0;
            // Avalonia may keep increasing ClickCount during a rapid sequence.
            if (args.ClickCount < 2 || args.ClickCount % 2 != 0) return;
            lastDoubleClick = now;
            if (++doubleClicks < 3) return;
            doubleClicks = 0; opening = true; args.Handled = true;
            try { await ExclusiveToolsActivationAsync(owner, unlock); }
            catch (Exception error)
            {
                AppDiagnostics.Record("Exclusive tool activation", error);
                if (owner.IsVisible) await Message(owner, "内部工具", error.Message);
            }
            finally { opening = false; }
        };
    }

    private static Task ExclusiveToolsActivationAsync(Window owner, Action unlock)
    {
        var dialog = new Window { Title = "内部工具", Width = 360, SizeToContent = SizeToContent.Height,
            CanResize = false, CanMinimize = false, CanMaximize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel();
        body.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcGap"));
        var password = Input();
        password.Watermark = Localization.Text("请输入内部口令");
        Localization.SetIsUserText(password, true);
        Avalonia.Automation.AutomationProperties.SetName(password, Localization.Text("内部口令"));
        body.Children.Add(password);
        var errorText = Text("", "caption");
        errorText.IsVisible = false;
        errorText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiDanger"));
        body.Children.Add(errorText);
        void Submit()
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password.Text ?? ""));
            if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(ExclusiveKeyDigest)))
            {
                errorText.Text = Localization.Text("口令错误，请重试");
                errorText.IsVisible = true; password.Classes.Add("error"); password.SelectAll(); password.Focus(); return;
            }
            try { unlock(); dialog.Close(); }
            catch (Exception error) { errorText.Text = error.Message; errorText.IsVisible = true; }
        }
        password.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            errorText.IsVisible = false; password.Classes.Remove("error");
        };
        password.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; Submit(); } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcRowGap"));
        buttons.Children.Add(DialogButton("取消", dialog.Close));
        buttons.Children.Add(DialogButton("激活", Submit, isDefault: true));
        body.Children.Add(buttons);
        dialog.Content = Controls.WindowChrome.WrapDialog(dialog, ComponentSheet(body));
        dialog.Opened += (_, _) => password.Focus();
        dialog.Closed += (_, _) => password.Text = "";
        return dialog.ShowDialog(owner);
    }

    internal static void UnlockExclusiveTools()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.Windows.OfType<MainWindow>().FirstOrDefault() is { } main)
        { main.UnlockExclusiveTools(); return; }
        var storage = new Storage();
        var settings = storage.LoadSettings();
        settings.ExclusiveToolUnlocked = true;
        storage.SaveSettings(settings);
        if (App.StartupSettings is { } startup) startup.ExclusiveToolUnlocked = true;
    }
}
