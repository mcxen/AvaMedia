using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AvaMedia.Controls;

/// <summary>组件的资源契约；宿主可在任意主题字典覆盖同名键。</summary>
public static class ComponentResources
{
    public static FuncValueConverter<double, double> ProgressChunk { get; } = new(width => width * .3);
    public static FuncValueConverter<double, double> ProgressEnd { get; } = new(width => width * .7);
    public const string ButtonTheme = "AcButton", ProgressTheme = "AcProgressBar", CardRadius = "AcCardRadius",
        CardPadding = "AcCardPadding", Gap = "AcGap", StageHeight = "AcStageHeight", ProgressHeight = "AcProgressHeight";
    public const string FieldHeight = "AcFieldHeight", FieldRadius = "AcFieldRadius", FieldPadding = "AcFieldPadding",
        FieldDisabledOpacity = "AcFieldDisabledOpacity", MultilineHeight = "AcMultilineHeight", FieldErrorMargin = "AcFieldErrorMargin",
        StatusHeight = "AcStatusHeight", StatusRadius = "AcStatusRadius", StatusPadding = "AcStatusPadding",
        StatusDotSize = "AcStatusDotSize", StatusGap = "AcStatusGap",
        SheetRadius = "AcSheetRadius", SheetPadding = "AcSheetPadding", SheetFace = "AcSheetFace",
        EmptyIconSize = "AcEmptyIconSize", EmptyGap = "AcEmptyGap", EmptyDetailWidth = "AcEmptyDetailWidth",
        EmptyPadding = "AcEmptyPadding", EmptyActionMargin = "AcEmptyActionMargin";
    public static DynamicResourceExtension Resource(string key) => new(key);
    public static void BindResource(AvaloniaObject control, AvaloniaProperty property, string key) => control.Bind(property, Resource(key));
}
