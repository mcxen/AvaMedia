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
using System.Text.RegularExpressions;

namespace AvaMedia.Desktop.AiProgress;

internal static class SpeechUi
{
    public static void ApplyWindowStyle(Window window)
    {
        // Scope the macOS 26 light tokens to these windows; other skins use their own resources.
        var light = new Dictionary<string, string>
        {
            ["UiCanvas"] = "#FAFAFC", ["UiSurface"] = "#FFFFFF", ["UiSurfaceRaised"] = "#F0F0F3",
            ["UiText"] = "#1D1D1F", ["UiTextSecondary"] = "#86868B", ["UiDivider"] = "#E5E5EA",
            ["UiBorder"] = "#E5E5EA", ["UiAccent"] = "#007AFF", ["UiAccentBorder"] = "#007AFF",
            ["UiAccentText"] = "#FFFFFF", ["UiSelection"] = "#EAF3FF", ["UiSelectionText"] = "#1D1D1F",
            ["UiSuccess"] = "#248A3D", ["UiDanger"] = "#FF3B30", ["UiWarning"] = "#FF9500",
            ["UiPrimaryHover"] = "#006FE8", ["UiPrimaryPressed"] = "#0064D1",
            ["UiSpeechStepSurface"] = "#F1F4FA", ["UiSpeechBackendSurface"] = "#1F34C759"
        };
        void Refresh()
        {
            foreach (var (key, value) in light)
                if (window.ActualThemeVariant == ThemeVariant.Light) window.Resources[key] = Brush.Parse(value);
                else window.Resources.Remove(key);
            if (window.ActualThemeVariant != ThemeVariant.Light)
            {
                window.Resources["UiSpeechStepSurface"] = window.FindResource("UiSurfaceRaised")!;
                window.Resources["UiSpeechBackendSurface"] = window.FindResource("UiSurface")!;
            }
        }
        window.ActualThemeVariantChanged += (_, _) => Refresh(); Refresh();
        window.Styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(16)),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 5)),
                new Setter(TemplatedControl.FontSizeProperty, 12.5) }
        });
        window.Styles.Add(new Style(x => x.OfType<Button>().Class("danger"))
        { Setters = { new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("UiDanger")) } });
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
    public static Border Card(Control child, double padding = 14)
    {
        var card = new Border { Child = child, Padding = new(padding), CornerRadius = new(14), BorderThickness = new(1) };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiDivider"));
        return card;
    }
    public static TextBlock Text(string value, bool secondary = false, double size = 13)
    {
        var text = Ui.Text(value, secondary ? "caption" : null); text.FontSize = size;
        return text;
    }
    public static TextBlock Heading(string value)
    { var text = Text(value); text.FontWeight = FontWeight.SemiBold; return text; }
    public static void CompactButton(Button button, double height = 28)
    {
        StableLayout.SetSamples(button, null); button.Width = double.NaN;
        button.MinWidth = 0; button.MinHeight = 0; button.Height = height;
        button.Classes.Remove("dialog-action"); button.Classes.Add("text-action");
        if (button.Classes.Contains("primary"))
        {
            button.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("UiAccent"));
            button.Bind(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("UiAccent"));
            button.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("UiAccentText"));
        }
    }
    public static void Detach(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Decorator decorator) decorator.Child = null;
        else if (control.Parent is ContentControl content) content.Content = null;
        Grid.SetColumn(control, 0); Grid.SetRow(control, 0); Grid.SetColumnSpan(control, 1); Grid.SetRowSpan(control, 1);
    }
    public static string ActivityText(string value)
    {
        var segment = Regex.Match(value, @"^第 (\d+) / (\d+) 段$");
        if (segment.Success) return Localization.Format($"第 {segment.Groups[1].Value} / {segment.Groups[2].Value} 段");
        var voices = Regex.Match(value, @"^检测到 (\d+) 个语音区间$");
        if (voices.Success) return Localization.Format($"检测到 {voices.Groups[1].Value} 个语音区间");
        return string.Join(" · ", value.Split(" · ").Select(Localization.Text));
    }
    public static string Model(AvaMedia.Core.SpeechModel model) => Localization.Text(model switch
    { AvaMedia.Core.SpeechModel.Tiny => "Whisper · 轻量", AvaMedia.Core.SpeechModel.Small => "Whisper · Small", _ => "Whisper · 标准" });
    public static string[] Languages => ["自动识别", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"];
}
