using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

internal sealed class FilePathChoice : UserControl
{
    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private string? _text;
    protected override Type StyleKeyOverride => typeof(UserControl);
    public string? Text
    {
        get => _text;
        set { _text = value; _label.Text = string.IsNullOrEmpty(value) ? "尚未选择" : PathInput.ShortName(value); ToolTip.SetTip(this, value); }
    }
    public FilePathChoice() { Content = _label; Text = ""; }
}
