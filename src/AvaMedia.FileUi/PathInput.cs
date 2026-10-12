using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.FileUi;

/// <summary>Compact path display with an explicit editable full address.</summary>
public sealed class PathInput : UserControl
{
    private readonly TextBox _editor = new() { IsVisible = false };
    private readonly TextBlock _caption = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly Button _display;
    private string? _originalText;
    protected override Type StyleKeyOverride => typeof(UserControl);
    public string? Text
    {
        get => _editor.Text;
        set { _editor.Text = value; UpdateCaption(); }
    }
    public PathInput()
    {
        _display = new Button { Content = _caption, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        var grid = new Grid(); grid.Children.Add(_display); grid.Children.Add(_editor); Content = grid;
        _display.Click += (_, _) => Edit();
        _editor.TextChanged += (_, _) => UpdateCaption();
        _editor.LostFocus += (_, _) => EndEdit();
        _editor.KeyDown += (_, args) =>
        {
            // Enter remains available to the containing file browser's navigation handler.
            if (args.Key == Key.Escape) { Text = _originalText; args.Handled = true; }
            if (args.Key is Key.Enter or Key.Escape) { EndEdit(); _display.Focus(); }
        };
        UpdateCaption();
    }
    private void Edit()
    {
        if (!_editor.IsVisible) _originalText = Text;
        _display.IsVisible = false; _editor.IsVisible = true;
        AutomationProperties.SetName(_editor, AutomationProperties.GetName(this) ?? "目录地址");
        _editor.Focus(); _editor.SelectAll();
    }
    private void EndEdit() { _editor.IsVisible = false; _display.IsVisible = true; UpdateCaption(); }
    public bool Focus() { Edit(); return _editor.IsFocused; }
    public void SelectAll() { Edit(); _editor.SelectAll(); }
    private void UpdateCaption()
    {
        _caption.Text = string.IsNullOrWhiteSpace(Text) ? "设置目录…" : ShortName(Text);
        ToolTip.SetTip(_display, string.IsNullOrWhiteSpace(Text) ? "点击输入完整路径" : Text + "\n点击编辑完整路径");
    }
    public static string ShortName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return path;
        var parts = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 ? "…/" + string.Join('/', parts.TakeLast(2)) : trimmed;
    }
}
