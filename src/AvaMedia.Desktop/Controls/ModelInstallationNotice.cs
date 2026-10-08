using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AvaMedia.Desktop.Controls;

public sealed class ModelInstallationNotice : Border
{
    private readonly TextBlock _message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _retry = new() { Content = "重试", Classes = { "field-action" } };
    private bool _preparing;
    public ModelInstallationNotice()
    {
        IsVisible = false;
        Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        Bind(PaddingProperty, new DynamicResourceExtension("UiStatusPadding"));
        _message.Classes.Add("caption");
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(_message); Grid.SetColumn(_retry, 1); row.Children.Add(_retry); Child = row;
        _retry.Click += async (_, _) => await ModelInstallation.StartAsync();
        AttachedToVisualTree += (_, _) =>
        {
            ModelInstallation.Changed += Refresh; Localization.Changed += LanguageChanged; Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ModelInstallation.Changed -= Refresh; Localization.Changed -= LanguageChanged;
        };
    }
    private void LanguageChanged(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        if (!ModelInstallation.Installing) _preparing = false;
        else if (ModelInstallation.Stage != "完成" && (ModelInstallation.Stage != "校验模型" || ModelInstallation.Received > 0)) _preparing = true;
        IsVisible = _preparing || ModelInstallation.Failed;
        _retry.IsVisible = ModelInstallation.Failed;
        _retry.Content = Localization.Text("重试");
        _message.Text = ModelInstallation.Failed ? Localization.Text("图片修复暂不可用，请检查网络后重试。")
            : Localization.Text("图片修复模型") + " · " + Localization.Text(ModelInstallation.Stage)
                + (ModelInstallation.Total > 0 ? $" · {ModelInstallation.Received / 1048576d:0.0} / {ModelInstallation.Total / 1048576d:0.0} MiB · {ModelInstallation.Percent}%" : "");
    }
}
