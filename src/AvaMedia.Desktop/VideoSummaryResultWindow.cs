using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed class VideoSummaryResultWindow : Window
{
    public VideoSummaryResultWindow(string folder)
    {
        Title = "视频总结结果"; Width = 1000; Height = 760; MinWidth = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "document");
        var root = new Grid { RowDefinitions = new("*,Auto"), Margin = new(20), RowSpacing = 16 };
        var tabs = new TabControl(); root.Children.Add(tabs);
        var notice = Ui.Text("读取结果…", "caption");
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16 }; footer.Children.Add(notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = "复制", IsEnabled = false };
        var export = new Button { Content = "另存为…", IsEnabled = false };
        var folderButton = new Button { Content = "打开目录" };
        folderButton.Click += (_, _) => { try { PlatformServices.OpenFolder(folder); } catch (Exception error) { notice.Text = error.Message; } };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (tabs.SelectedItem is TabItem { Content: TextBox text } && Clipboard is { } clipboard)
                { await clipboard.SetTextAsync(text.Text); notice.Text = Localization.Text("已复制"); }
            }
            catch (Exception error) { notice.Text = error.Message; }
        };
        export.Click += async (_, _) =>
        {
            try
            {
                if (tabs.SelectedItem is not TabItem { Content: TextBox text } tab) return;
                var extension = tab.Tag as string ?? "md";
                var target = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("保存结果"),
                    SuggestedFileName = "video-summary." + extension, DefaultExtension = extension });
                if (target?.TryGetLocalPath() is { } path) await File.WriteAllTextAsync(path, text.Text ?? "", new UTF8Encoding(false));
            }
            catch (Exception error) { notice.Text = error.Message; }
        };
        actions.Children.Add(copy); actions.Children.Add(export); actions.Children.Add(folderButton); actions.Children.Add(Ui.DialogButton("关闭", Close));
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        var lifetime = new CancellationTokenSource(); Closed += (_, _) => lifetime.Cancel();
        Opened += async (_, _) =>
        {
            try
            {
                var path = Path.Combine(folder, "report.json");
                if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("结果超过 32 MB，请从输出目录打开。");
                var report = JsonSerializer.Deserialize<VideoSummaryReport>(await File.ReadAllTextAsync(path, lifetime.Token))
                    ?? throw new InvalidDataException("视频总结结果无效。");
                var items = new List<TabItem>();
                foreach (var section in report.Sections) AddTab(section.Title, section.Text, "md");
                foreach (var entry in new[] { ("字幕", "subtitles.srt", "srt"), ("逐字稿", "transcript.txt", "txt") })
                {
                    var subtitle = Path.Combine(folder, entry.Item2);
                    if (!File.Exists(subtitle)) continue;
                    if (new FileInfo(subtitle).Length > 32 * 1024 * 1024) throw new InvalidDataException("字幕超过 32 MB，请从输出目录打开。");
                    AddTab(entry.Item1, await File.ReadAllTextAsync(subtitle, lifetime.Token), entry.Item3);
                }
                if (report.Frames.Count > 0) AddTab("画面观察", string.Join("\n\n", report.Frames.Select(frame =>
                    $"{MediaTime.Format(frame.Seconds)}\n{frame.Description}")), "txt");
                AddTab("结果范围", string.Join("\n\n", report.Limitations), "txt");
                if (lifetime.IsCancellationRequested) return;
                tabs.ItemsSource = items; tabs.SelectedIndex = 0; copy.IsEnabled = export.IsEnabled = true;
                notice.Text = report.Source; Localization.SetIsUserText(notice, true);
                void AddTab(string title, string value, string extension)
                {
                    var text = new TextBox { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                    Localization.SetIsUserText(text, true); items.Add(new TabItem { Header = title, Content = text, Tag = extension });
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { if (!lifetime.IsCancellationRequested) notice.Text = error.Message; }
        };
    }
}
