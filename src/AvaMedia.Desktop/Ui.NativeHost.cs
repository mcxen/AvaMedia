using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static partial class Ui
{
    /// <summary>Check before creating or attaching a browser, including XAML-created views.</summary>
    internal static string? NativeBrowserUnavailableReason()
    {
        // The desktop browser currently supports Windows and macOS. Do not probe
        // WebKitGTK by creating an adapter: missing native libraries throw async void.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return Localization.Text("浏览器嗅探当前支持 macOS 和 Windows。");
        try
        {
            return OperatingSystem.IsWindows()
                && !WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsInstalled
                    ? Localization.Text("需要安装 Microsoft Edge WebView2 Runtime。") : null;
        }
        catch (Exception error)
        {
            AppDiagnostics.Record("Browser capability", error);
            return DownloadDiagnostics.Redact(error.Message);
        }
    }

    internal static void GuardNativeBrowserHost(Window window, Control content)
    {
        var browsers = content.GetLogicalDescendants().OfType<NativeWebView>().ToList();
        if (content is NativeWebView browser) browsers.Add(browser);
        if (browsers.Count == 0 || NativeBrowserUnavailableReason() is not { } reason) return;
        foreach (var view in browsers)
        {
            // Keep the named instance alive for existing business event wiring, but
            // remove it from the tree before NativeWebView.OnAttached can run.
            var empty = ComponentEmptyState("浏览器嗅探", isError: true);
            empty.Detail = reason;
            Localization.SetIsUserText(empty, true);
            switch (view.Parent)
            {
                case Decorator decorator when ReferenceEquals(decorator.Child, view):
                    decorator.Child = empty;
                    break;
                case Panel panel:
                    var index = panel.Children.IndexOf(view);
                    panel.Children.Remove(view);
                    Grid.SetRow(empty, Grid.GetRow(view)); Grid.SetColumn(empty, Grid.GetColumn(view));
                    Grid.SetRowSpan(empty, Grid.GetRowSpan(view)); Grid.SetColumnSpan(empty, Grid.GetColumnSpan(view));
                    panel.Children.Insert(index, empty);
                    break;
                case ContentControl host when ReferenceEquals(host.Content, view):
                    host.Content = empty;
                    break;
                case null when ReferenceEquals(content, view):
                    window.Content = empty;
                    break;
            }
        }
    }
}
