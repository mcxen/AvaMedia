using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly Border _captionPanel = ChartPanel();
    private readonly Expander _captionObservations = new() { Header = "画面观察", HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private void RenderCaption(MediaTagResult? result)
    {
        _captionPanel.IsVisible = false; _captionPanel.Child = null;
        if (result is null) return;
        result = MediaPrivacy.Filter(result, _settings.EnableNsfwContent);
        if (string.IsNullOrWhiteSpace(result.Caption) && result.CaptionError is null && result.CaptionObservations.Count == 0) return;
        var content = new StackPanel { Spacing = 8 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        heading.Children.Add(Ui.Text("内容总结", "heading"));
        if (!string.IsNullOrWhiteSpace(result.Caption))
        {
            var text = result.Caption;
            var copy = Ui.Button("复制", async () =>
            {
                try { if (Clipboard is not null) await Clipboard.SetTextAsync(text); }
                catch (Exception error) { if (!_closed) await Ui.Message(this, "复制失败", error.Message); }
            });
            Grid.SetColumn(copy, 1); heading.Children.Add(copy);
            content.Children.Add(heading);
            var body = Ui.Text(text); body.TextWrapping = TextWrapping.Wrap; Localization.SetIsUserText(body, true);
            content.Children.Add(body);
        }
        else content.Children.Add(heading);
        if (result.CaptionError is not null) content.Children.Add(Ui.Text(Localization.Text("画面描述失败：") + result.CaptionError, "error"));
        if (result.CaptionModel is { } model)
        {
            var name = ModelCatalog.All.FirstOrDefault(definition => definition.Id == model)?.Name ?? model;
            var label = Ui.Text(name + (result.CaptionBackend is { } backend ? " · " + backend : ""), "caption");
            Localization.SetIsUserText(label, true); content.Children.Add(label);
        }
        if (result.CaptionObservations.Count > 0)
        {
            var observations = new StackPanel { Spacing = 6 };
            foreach (var observation in result.CaptionObservations)
            {
                var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
                if (VideoFormats.IsVideo(result.Path))
                    row.Children.Add(Ui.Button(MediaTime.Format(observation.Seconds), async () => await SelectSampleAsync(observation.Seconds)));
                else row.Children.Add(Ui.Text(MediaTime.Format(observation.Seconds), "caption"));
                var text = Ui.Text(observation.Text); text.TextWrapping = TextWrapping.Wrap; Localization.SetIsUserText(text, true);
                Grid.SetColumn(text, 1); row.Children.Add(text); observations.Children.Add(row);
            }
            // Preserve expansion while live observations update the current result.
            if (_captionObservations.Parent is Panel previous) previous.Children.Remove(_captionObservations);
            _captionObservations.Content = observations; content.Children.Add(_captionObservations);
        }
        _captionPanel.Child = content; _captionPanel.IsVisible = true;
    }
}
