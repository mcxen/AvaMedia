using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>One scrollable flow; node snapshots come from actual processing stages.</summary>
internal sealed class AiActivityFlowView : StackPanel
{
    private readonly List<NodeView> _nodes = [];
    private readonly List<FlowConnector> _connectors = [];
    private DateTime _startedUtc;
    private int _current = -1;
    public void Clear()
    {
        foreach (var node in _nodes) node.ReleasePreview();
        Children.Clear(); _nodes.Clear(); _connectors.Clear(); _current = -1;
    }
    public void Update(AiActivity activity)
    {
        var nodes = activity.Nodes.Length > 0 ? activity.Nodes : [new AiActivityNode(activity.Model, activity)];
        if (_startedUtc != activity.StartedUtc || _nodes.Count != nodes.Length)
        {
            Clear(); _startedUtc = activity.StartedUtc;
            for (var index = 0; index < nodes.Length; index++)
            {
                if (index > 0)
                {
                    var connector = new FlowConnector(); _connectors.Add(connector); Children.Add(connector);
                }
                var node = new NodeView(index + 1, index + 1 < nodes.Length); _nodes.Add(node); Children.Add(node);
            }
        }
        var current = Math.Clamp(activity.CurrentNode, 0, nodes.Length - 1);
        for (var index = 0; index < nodes.Length; index++)
        {
            var snapshot = nodes[index].Snapshot;
            if (index == current && snapshot is not null)
                snapshot = activity with { StartedUtc = snapshot.StartedUtc, Nodes = [] };
            var progressed = _nodes[index].Update(nodes[index].Title, snapshot);
            if (current != _current) _nodes[index].Expand(index == current);
            if (index > 0)
            {
                var connector = _connectors[index - 1];
                connector.SetReached(snapshot is not null);
                if (progressed && index == current && activity.State == AiActivityState.Running) connector.Pulse();
            }
        }
        var changed = _current != current;
        _current = current;
        if (changed) RevealCurrent();
    }
    public void RevealCurrent()
    {
        if (_current >= 0 && _current < _nodes.Count) _nodes[_current].Reveal();
    }
    public void RefreshClock(DateTime now)
    {
        foreach (var node in _nodes) node.RefreshClock(now);
    }
    private sealed class NodeView : Grid
    {
        private readonly int _number;
        private readonly TextBlock _markerText = AiActivityView.Text();
        private readonly Border _marker = new() { Width = 28, Height = 28, CornerRadius = new(14), BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 10, 0, 0) };
        private readonly Border _card = new() { BorderThickness = new(1), CornerRadius = new(3), Padding = new(10, 8) };
        private readonly FlowConnector _tail = new() { Height = double.NaN, TopOffset = 38, ShowArrow = false };
        private readonly Expander _expander = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _title = AiActivityView.Text();
        private readonly TextBlock _status = AiActivityView.Text();
        private readonly TextBlock _stage = AiActivityView.Text();
        private readonly TextBlock _meta = AiActivityView.Text();
        private readonly TextBlock _latest = AiActivityView.Text();
        private readonly TextBlock _history = AiActivityView.Text();
        private readonly TextBlock _caption = AiActivityView.Text();
        private readonly ProgressBar _progress = new() { Maximum = 100, Height = 6 };
        private readonly Image _image = new() { Height = 132, Stretch = Stretch.Uniform, IsVisible = false };
        private readonly Expander _observations = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private AiActivity? _snapshot;
        private Bitmap? _bitmap;
        private byte[]? _preview;
        private bool _attached;
        public NodeView(int number, bool hasNext)
        {
            _number = number; ColumnDefinitions = new("36,*"); ColumnSpacing = 8;
            _tail.IsVisible = hasNext; Children.Add(_tail);
            _markerText.HorizontalAlignment = HorizontalAlignment.Center; _markerText.VerticalAlignment = VerticalAlignment.Center;
            _marker.Child = _markerText; Children.Add(_marker);
            _title.FontWeight = FontWeight.SemiBold;
            _title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
            _status.VerticalAlignment = VerticalAlignment.Center;
            var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
            header.Children.Add(_title); Grid.SetColumn(_status, 1); header.Children.Add(_status);
            _expander.Header = header;
            var body = new StackPanel { Spacing = 7, Margin = new(0, 8, 0, 0) };
            body.Children.Add(_stage); body.Children.Add(_progress); body.Children.Add(_meta);
            body.Children.Add(_image); body.Children.Add(_caption); body.Children.Add(_latest);
            _latest.MaxLines = 5; _latest.TextTrimming = TextTrimming.CharacterEllipsis;
            _observations.Content = _history; body.Children.Add(_observations); _expander.Content = body;
            _card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurface"));
            _card.Child = _expander; Grid.SetColumn(_card, 1); Children.Add(_card);
            _expander.PropertyChanged += (_, change) => { if (change.Property == Expander.IsExpandedProperty) RefreshPreview(); };
            AttachedToVisualTree += (_, _) => { _attached = true; RefreshPreview(); };
            DetachedFromVisualTree += (_, _) => { _attached = false; ReleasePreview(); };
        }
        public bool Update(string title, AiActivity? snapshot)
        {
            var progressed = snapshot is not null && _snapshot is not null &&
                (snapshot.Stage != _snapshot.Stage || snapshot.Current != _snapshot.Current || snapshot.ResultCount != _snapshot.ResultCount);
            _snapshot = snapshot; _title.Text = Localization.Text(title);
            _expander.IsEnabled = snapshot is not null;
            var state = snapshot?.State;
            var color = state switch { AiActivityState.Running => "UiAccent", AiActivityState.Completed => "UiSuccess", AiActivityState.Failed => "UiDanger", AiActivityState.Cancelled => "UiWarning", _ => "UiTextSecondary" };
            _markerText.Text = state == AiActivityState.Completed ? "✓" : _number.ToString();
            _markerText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(color));
            _marker.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(color));
            _marker.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurface"));
            _card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(state == AiActivityState.Running ? "UiAccent" : "UiDivider"));
            _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(color));
            _tail.SetReached(snapshot is not null);
            if (snapshot is null) { _status.Text = Localization.Text("待处理"); ReleasePreview(); return progressed; }
            _stage.Text = string.Join(" · ", new[] { Localization.Text(snapshot.Stage), AiActivityView.Count(snapshot) }.Where(text => text.Length > 0));
            _progress.IsVisible = state == AiActivityState.Running && AiActivityView.Quantified(snapshot);
            _progress.Value = AiActivityView.Quantified(snapshot) ? Math.Clamp(snapshot.Current!.Value / snapshot.Total!.Value * 100, 0, 100) : 0;
            var results = snapshot.ResultCount > 0 ? Localization.Format($"{snapshot.ResultCount} {Localization.Key(snapshot.ResultLabel)}") : "";
            _meta.Text = string.Join(" · ", new[] { Localization.Text(snapshot.Model), snapshot.Backend, results, Localization.Text(snapshot.Detail) }.Where(text => text.Length > 0));
            _latest.Text = string.Join(Environment.NewLine, snapshot.RecentResults.TakeLast(2)); _latest.IsVisible = _latest.Text.Length > 0;
            _history.Text = string.Join(Environment.NewLine, snapshot.RecentStages)
                + (snapshot.RecentResults.Length > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, snapshot.RecentResults) : "");
            _observations.Header = snapshot.RecentResults.Length > 0 ? Localization.Format($"阶段结果 · 最近 {snapshot.RecentResults.Length} 条") : Localization.Text("过程记录");
            _observations.IsVisible = _history.Text.Length > 0;
            RefreshClock(snapshot.State == AiActivityState.Running ? DateTime.UtcNow : snapshot.UpdatedUtc); RefreshPreview();
            return progressed;
        }
        public void Expand(bool expanded) => _expander.IsExpanded = expanded;
        public void Reveal()
        {
            if (_attached) Dispatcher.UIThread.Post(() => { if (_attached) _expander.BringIntoView(); }, DispatcherPriority.Background);
        }
        public void RefreshClock(DateTime now)
        {
            if (_snapshot is not { } snapshot) return;
            var state = snapshot.State switch { AiActivityState.Completed => "已完成", AiActivityState.Failed => "失败", AiActivityState.Cancelled => "已停止", _ => "进行中" };
            var until = snapshot.State == AiActivityState.Running ? now : snapshot.UpdatedUtc;
            _status.Text = Localization.Text(state) + " · " + AiActivity.FormatElapsed((until - snapshot.StartedUtc).TotalSeconds);
        }
        private void RefreshPreview()
        {
            if (!_attached || !_expander.IsExpanded || _snapshot?.Preview is not { } png) { ReleasePreview(); return; }
            if (!ReferenceEquals(png, _preview))
            {
                ReleasePreview();
                try { using var stream = new MemoryStream(png); _bitmap = Bitmap.DecodeToWidth(stream, 360); _preview = png; _image.Source = _bitmap; }
                catch (Exception error) when (error is ArgumentException or InvalidDataException) { }
            }
            _image.IsVisible = _bitmap is not null;
            _caption.Text = _snapshot.PreviewCaption; _caption.IsVisible = _image.IsVisible && _caption.Text.Length > 0;
        }
        public void ReleasePreview()
        {
            _image.Source = null; _bitmap?.Dispose(); _bitmap = null; _preview = null;
            _image.IsVisible = _caption.IsVisible = false;
        }
    }
    private sealed class FlowConnector : Control
    {
        private static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<FlowConnector, IBrush?>("Stroke");
        private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private DateTime _pulseStarted;
        private bool _attached;
        private bool _reached;
        public double TopOffset { get; init; }
        public bool ShowArrow { get; init; } = true;
        public FlowConnector()
        {
            Height = 22;
            Bind(StrokeProperty, new DynamicResourceExtension("UiDivider"));
            _pulse.Tick += (_, _) => { if ((DateTime.UtcNow - _pulseStarted).TotalMilliseconds >= 600) _pulse.Stop(); InvalidateVisual(); };
            AttachedToVisualTree += (_, _) => _attached = true;
            DetachedFromVisualTree += (_, _) => { _attached = false; _pulse.Stop(); };
        }
        public void SetReached(bool reached)
        {
            if (_reached == reached) return;
            _reached = reached; Bind(StrokeProperty, new DynamicResourceExtension(reached ? "UiAccent" : "UiDivider"));
        }
        public void Pulse()
        {
            if (!_attached || _pulse.IsEnabled) return;
            _pulseStarted = DateTime.UtcNow; _pulse.Start();
        }
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == StrokeProperty) InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var brush = GetValue(StrokeProperty); var x = 18d; var end = Bounds.Height - 3;
            if (end <= TopOffset) return;
            context.DrawLine(new Pen(brush, 1, new DashStyle([3, 3], 0)), new(x, TopOffset), new(x, end));
            if (ShowArrow)
            {
                var arrow = new Pen(brush, 1); context.DrawLine(arrow, new(x - 3, end - 3), new(x, end)); context.DrawLine(arrow, new(x + 3, end - 3), new(x, end));
            }
            if (_pulse.IsEnabled) context.DrawEllipse(brush, null, new(x, end * Math.Clamp((DateTime.UtcNow - _pulseStarted).TotalMilliseconds / 600, 0, 1)), 2.5, 2.5);
        }
    }
}
