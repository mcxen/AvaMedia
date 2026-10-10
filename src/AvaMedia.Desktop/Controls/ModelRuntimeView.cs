using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Small, live status badge. Unknown native load duration is shown as elapsed time, never a fake percentage.</summary>
internal sealed class ModelRuntimeView : Grid
{
    private readonly string _root, _id;
    private readonly Border _light = new() { Width = 18, Height = 18, CornerRadius = new(9), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _symbol = new() { FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = Ui.Text("", "caption");
    private readonly TextBlock _detail = Ui.Text("", "caption");
    private readonly ProgressBar _progress = new() { Height = 3, MinHeight = 3, IsIndeterminate = true, IsVisible = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _attached;
    public ModelRuntimeView(string root, string id, string? label = null)
    {
        _root = root; _id = id;
        ColumnDefinitions = new("Auto,*"); RowDefinitions = new("Auto,3"); ColumnSpacing = 7; RowSpacing = 4;
        _light.Child = _symbol; Children.Add(_light);
        var text = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 8 };
        if (label is not null) text.Children.Add(Ui.Text(label, "caption"));
        StableLayout.Reserve(_state, "未加载", "加载中", "预热中", "已就绪", "使用中", "加载失败");
        _detail.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        _state.TextWrapping = _detail.TextWrapping = TextWrapping.NoWrap;
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_state, 1); Grid.SetColumn(_detail, 2);
        text.Children.Add(_state); text.Children.Add(_detail); Grid.SetColumn(text, 1); Children.Add(text);
        Grid.SetRow(_progress, 1); Grid.SetColumn(_progress, 1); Children.Add(_progress);
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            if (_attached) return; _attached = true;
            MediaTagRuntime.Changed += RuntimeChanged; Localization.Changed += LanguageChanged; Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false; _timer.Stop(); MediaTagRuntime.Changed -= RuntimeChanged; Localization.Changed -= LanguageChanged;
        };
        Refresh();
    }
    private void RuntimeChanged(string root)
    {
        if (BatchRename.PathComparer.Equals(root, _root)) Dispatcher.UIThread.Post(() => { if (_attached) Refresh(); });
    }
    private void LanguageChanged(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        var value = MediaTagRuntime.Status(_root, _id);
        var (title, symbol, color) = value.State switch
        {
            ModelLoadState.Loading => ("加载中", "↓", "#2379B5"),
            ModelLoadState.Warming => ("预热中", "◌", "#2379B5"),
            ModelLoadState.Ready => ("已就绪", "✓", "#24835A"),
            ModelLoadState.InUse => ("使用中", "▶", "#2379B5"),
            ModelLoadState.Failed => ("加载失败", "!", "#C74444"),
            _ => ("未加载", "—", "#7A7A7A")
        };
        _state.Text = Localization.Text(title); _symbol.Text = symbol; _light.Background = Brush.Parse(color);
        _detail.Text = value.Preparing && value.StartedUtc is { } start
            ? AiActivity.FormatElapsed((DateTime.UtcNow - start).TotalSeconds)
            : value.State == ModelLoadState.Ready && value.ReleaseUtc is { } release
                ? Localization.Format($"{value.Backend} · {AiActivity.FormatElapsed(Math.Max(0, (release - DateTime.UtcNow).TotalSeconds))} 后释放")
                : value.Loaded ? value.Backend : "";
        _progress.IsVisible = value.Preparing;
        var ticking = _attached && (value.Preparing || value.State == ModelLoadState.Ready && value.ReleaseUtc is not null);
        if (ticking) _timer.Start(); else _timer.Stop();
        ToolTip.SetTip(this, value.Error);
        Avalonia.Automation.AutomationProperties.SetName(this, Localization.Text(ModelCatalog.Find(_id).Name) + " · " + _state.Text + " · " + _detail.Text);
    }
}
