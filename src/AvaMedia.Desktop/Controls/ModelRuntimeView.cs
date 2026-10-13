using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Small, live status badge. Unknown native load duration is shown as elapsed time, never a fake percentage.</summary>
internal sealed class ModelRuntimeView : Grid
{
    private readonly string _root, _id;
    private readonly ModelStateIndicator _light = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = Ui.Text("", "caption");
    private readonly TextBlock _detail = Ui.Text("", "caption");
    private readonly ProgressBar _progress = new() { Height = 4, MinHeight = 4, Maximum = 100, IsVisible = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _attached;
    private ModelLoadState? _lastState;
    public ModelRuntimeView(string root, string id, string? label = null)
    {
        _root = root; _id = id;
        _light.Bind(ModelStateIndicator.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        ColumnDefinitions = new("Auto,*"); RowDefinitions = new("Auto,4"); ColumnSpacing = 7; RowSpacing = 4;
        Children.Add(_light);
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
        var (title, color) = value.State switch
        {
            ModelLoadState.Loading => ("加载中", "UiAccent"),
            ModelLoadState.Warming => ("预热中", "UiAccent"),
            ModelLoadState.Ready => ("已就绪", "UiSuccess"),
            ModelLoadState.InUse => ("使用中", "UiAccent"),
            ModelLoadState.Failed => ("加载失败", "UiDanger"),
            _ => ("未加载", "UiTextSecondary")
        };
        var changed = _lastState.HasValue && _lastState != value.State;
        _lastState = value.State; _state.Text = Localization.Text(title); _light.Update(value.State);
        if (_stateColor != color) { _stateColor = color; _light.Bind(ModelStateIndicator.ForegroundProperty, new DynamicResourceExtension(color)); }
        if (changed) { Motion.Reveal(_state); Motion.Reveal(_light); }
        _detail.Text = value.Preparing && value.StartedUtc is { } start
            ? AiActivity.FormatElapsed((DateTime.UtcNow - start).TotalSeconds)
            : value.State == ModelLoadState.Ready && value.ReleaseUtc is { } release
                ? Localization.Format($"{value.Backend} · {AiActivity.FormatElapsed(Math.Max(0, (release - DateTime.UtcNow).TotalSeconds))} 后释放")
                : value.Loaded ? value.Backend : "";
        _progress.IsVisible = value.Preparing;
        _progress.IsIndeterminate = value.Preparing && value.Preparation?.Fraction is null;
        _progress.Value = (value.Preparation?.Fraction ?? 0) * 100;
        var ticking = _attached && (value.Preparing || value.State == ModelLoadState.Ready && value.ReleaseUtc is not null);
        if (ticking) _timer.Start(); else _timer.Stop();
        var preparation = value.Preparing && value.Preparation is { } stage
            ? Localization.Text(stage.Stage) + (stage.Fraction is { } fraction ? $" · {fraction * 100:0}%" : "") : "";
        var description = string.Join(" · ", new[] { Localization.Text(ModelCatalog.Find(_id).Name), _state.Text, _detail.Text, preparation, value.Error }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        ToolTip.SetTip(this, description);
        Avalonia.Automation.AutomationProperties.SetName(this, description);
    }
    private string? _stateColor;
}
