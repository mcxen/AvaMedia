using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.AiProgress;

internal sealed class SpeechStageCard : Border
{
    private readonly TextBlock _elapsed = SpeechUi.Text("", true, 10.5);
    private readonly Border _number;
    private readonly StackPanel _steps = new() { Spacing = 6 };
    private AiActivity? _shown;
    private bool _wasCurrent;
    private JobState _shownState;
    public SpeechStageCard(int index)
    {
        Padding = new(12, 10); CornerRadius = new(12); BorderThickness = new(1); Height = 162;
        this.Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        var root = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        var header = new Grid { ColumnDefinitions = new("18,*,Auto"), ColumnSpacing = 7 };
        var number = SpeechUi.Text((index + 1).ToString(), size: 10.5); number.Foreground = Brushes.White; number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center;
        _number = new Border { Child = number, Width = 18, Height = 18, CornerRadius = new(9) }; header.Children.Add(_number);
        var title = SpeechUi.Heading(SpeechUi.Stages[index]); title.FontSize = 12.5; Grid.SetColumn(title, 1); header.Children.Add(title);
        Grid.SetColumn(_elapsed, 2); header.Children.Add(_elapsed); root.Children.Add(header);
        var scroll = new ScrollViewer { Content = _steps, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll); Child = root;
    }
    public void Update(AiActivity? snapshot, bool current, JobState state, bool languageChanged)
    {
        _elapsed.Text = snapshot is null ? "" : AiActivity.FormatElapsed(((current && state == JobState.Running ? DateTime.UtcNow : snapshot.UpdatedUtc) - snapshot.StartedUtc).TotalSeconds);
        if (ReferenceEquals(_shown, snapshot) && current == _wasCurrent && state == _shownState && !languageChanged) return;
        _shown = snapshot; _wasCurrent = current; _shownState = state;
        var color = snapshot is null ? "UiTextSecondary" : snapshot.State == AiActivityState.Completed ? "UiSuccess"
            : state == JobState.Failed && current ? "UiDanger" : state == JobState.Paused && current ? "UiWarning" : "UiAccent";
        _number.Bind(BackgroundProperty, new DynamicResourceExtension(color));
        this.Bind(BorderBrushProperty, new DynamicResourceExtension(current ? "UiAccent" : "UiDivider")); BorderThickness = new(current ? 1.5 : 1);
        _steps.Children.Clear();
        if (snapshot is null) { _steps.Children.Add(SpeechUi.Text("等待中", true, 11.5)); return; }
        var entries = snapshot.RecentStages.Select(entry =>
        {
            var split = entry.IndexOf(" · ", StringComparison.Ordinal); return split < 0 ? entry : entry[(split + 3)..];
        }).Append(snapshot.Detail.StartsWith(snapshot.Stage + " · ", StringComparison.Ordinal) ? snapshot.Detail : snapshot.Stage)
            .GroupBy(entry => entry.Split(" · ")[0]).Select(group => group.Last()).TakeLast(12).ToArray();
        foreach (var entry in entries)
        {
            var active = entry == snapshot.Stage || entry.StartsWith(snapshot.Stage + " · ", StringComparison.Ordinal);
            var row = new Grid { ColumnDefinitions = new("7,*"), ColumnSpacing = 7 };
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new(4), VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(BackgroundProperty, new DynamicResourceExtension(active ? color : "UiSuccess")); row.Children.Add(dot);
            var text = SpeechUi.Text(SpeechUi.ActivityText(entry), size: 11.5); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            if (active && current) text.FontWeight = FontWeight.SemiBold;
            if (active && snapshot.Unit == "秒" && snapshot.Current is { } position && snapshot.Total is { } duration)
                text.Text = Localization.Text(snapshot.Stage) + " · " + SpeechTimeline.TimeLabel(position) + " / " + SpeechTimeline.TimeLabel(duration);
            else if (active && snapshot.Detail.Length > 0 && !entry.Contains(snapshot.Detail, StringComparison.Ordinal))
                text.Text += " · " + SpeechUi.ActivityText(snapshot.Detail);
            ToolTip.SetTip(text, text.Text); Grid.SetColumn(text, 1); row.Children.Add(text); _steps.Children.Add(row);
        }
    }
}
