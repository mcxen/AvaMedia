using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;

namespace AvaMedia.Desktop.Speech;

internal static class SpeechUi
{
    public static void ApplyWindowStyle(Window window)
    {
        // 通用控件和调色板由应用资源统一提供，不在窗口里覆写颜色与尺寸。
        window.Classes.Add("component-window");
    }

    public static void PreserveChoiceFace(ToggleButton button)
    {
        // Bind the face at template priority so the theme's checked/hover presenter
        // colors cannot replace the blue selection or its white text.
        button.Template = new FuncControlTemplate<ToggleButton>((parent, scope) =>
        {
            var face = new ContentPresenter { Name = "ChoiceFace" };
            face.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.PaddingProperty, new TemplateBinding(TemplatedControl.PaddingProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.CornerRadiusProperty, new TemplateBinding(TemplatedControl.CornerRadiusProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.BackgroundProperty, new TemplateBinding(TemplatedControl.BackgroundProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.ForegroundProperty, new TemplateBinding(TemplatedControl.ForegroundProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.BorderBrushProperty, new TemplateBinding(TemplatedControl.BorderBrushProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.BorderThicknessProperty, new TemplateBinding(TemplatedControl.BorderThicknessProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.HorizontalContentAlignmentProperty, new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty), BindingPriority.Template);
            face.Bind(ContentPresenter.VerticalContentAlignmentProperty, new TemplateBinding(ContentControl.VerticalContentAlignmentProperty), BindingPriority.Template);
            return face;
        });
    }

    public static void SetChoiceState(ToggleButton button, IEnumerable<TextBlock>? labels = null)
    {
        var selected = button.IsChecked == true;
        button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        button.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension(selected ? "UiAccent" : "UiSurface"));
        button.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension(selected ? "UiAccentText" : "UiText"));
        button.Bind(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension(selected ? "UiAccent" : "UiDivider"));
        if (labels is not null)
            foreach (var label in labels) label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(selected ? "UiAccentText" : "UiText"));
    }

    public static readonly string[] Stages = ["读取音轨", "语音模型", "语音识别", "保存结果"];
    public static Border Card(Control child, double padding = 16)
    {
        var card = new AvaMedia.Controls.Card { Child = child };
        if (padding != 16) card.Padding = new(padding);
        return card;
    }
    public static TextBlock Text(string value, bool secondary = false, double size = 13)
    {
        var text = Ui.Text(value, secondary ? "caption" : null); text.FontSize = Math.Ceiling(size);
        var font = Math.Ceiling(size) switch { 11 => "UiFontTiny", 12 => "UiFontCaption", 13 => "UiFontBody", _ => null };
        if (font is not null) text.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension(font));
        return text;
    }
    public static TextBlock Heading(string value)
    { var text = Text(value); text.FontWeight = FontWeight.SemiBold; return text; }
    public static void CompactButton(Button button, double height = 28)
    {
        StableLayout.SetSamples(button, null); button.Width = double.NaN;
        button.MinWidth = 0;
        button.Classes.Remove("dialog-action"); button.Classes.Add("ac"); button.Classes.Add("text-action");
        button.Bind(Layoutable.HeightProperty, new DynamicResourceExtension("AcButtonHeight"));
        if (height != 28) button.Height = height;
    }
    public static void CompactRows(ListBox list)
    {
        list.Padding = new(0);
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = {
            new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
            new Setter(Layoutable.MinHeightProperty, 0d),
            new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
    }
    public static void Detach(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Decorator decorator) decorator.Child = null;
        else if (control.Parent is ContentControl content) content.Content = null;
        Grid.SetColumn(control, 0); Grid.SetRow(control, 0); Grid.SetColumnSpan(control, 1); Grid.SetRowSpan(control, 1);
    }
    public static string Model(AvaMedia.Core.SpeechModel model) => Localization.Text(model switch
    { AvaMedia.Core.SpeechModel.Tiny => "Whisper · 轻量", AvaMedia.Core.SpeechModel.Small => "Whisper · Small", _ => "Whisper · 标准" });
    public static string[] Languages => ["自动识别", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"];
}
