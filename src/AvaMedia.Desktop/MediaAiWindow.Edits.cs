using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly Dictionary<string, ResultTag[]> _editedTags = new(BatchRename.PathComparer);
    private sealed record Preferences(decimal Threshold, decimal Frames, bool Gpu, bool Reuse, bool Recursive, bool Scores, bool OnlyLibrary);
    private void LoadPreferences()
    {
        if(new Storage().LoadToolOptions<Preferences>("media-ai") is not {} saved) return;
        _threshold.Value = Math.Clamp(saved.Threshold, .05m, .95m); _frames.Value = Math.Clamp(saved.Frames, 1, 32);
        _gpu.IsChecked = saved.Gpu; _reuse.IsChecked = saved.Reuse; _recursive.IsChecked = saved.Recursive;
        _showScores.IsChecked = saved.Scores; _onlyLibrary.IsChecked = saved.OnlyLibrary;
    }
    private void SavePreferences() => new Storage().SaveToolOptions("media-ai", new Preferences(_threshold.Value ?? .4m, _frames.Value ?? 8,
        _gpu.IsChecked == true, _reuse.IsChecked == true, _recursive.IsChecked == true, _showScores.IsChecked == true, _onlyLibrary.IsChecked == true));
    private async Task EditTagsAsync(MediaTagResult result)
    {
        if(_busy) return;
        var original = ResultTags(result).ToArray();
        var input = Ui.Input(string.Join(Environment.NewLine, original.Select(tag => tag.Label)));
        input.AcceptsReturn = true; input.TextWrapping = Avalonia.Media.TextWrapping.Wrap; Localization.SetIsUserText(input, true);
        var window = new Window { Title = "编辑标签", Width = 480, Height = 420, MinWidth = 360, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12, Margin = new(20) }; root.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Ui.DialogButton("取消", window.Close));
        actions.Children.Add(Ui.DialogButton("保存", () =>
        {
            _editedTags[result.Path] = (input.Text ?? "").Split(['\r', '\n', ',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(label => original.FirstOrDefault(tag => tag.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) ?? new ResultTag(label, "手动标签", 1)).ToArray();
            window.Close(); RefreshDisplayedResults();
        }));
        Grid.SetRow(actions, 1); root.Children.Add(actions); window.Content = root; await window.ShowDialog(this);
    }
    private async Task ShowEvidenceAsync(MediaTagResult result, ResultTag tag)
    {
        var matches = result.Frames.Where(frame => frame.Scores.Any(score => WordLibraryCatalog.TagLabel(score.Tag) == tag.Label && score.Score >= (double)(_threshold.Value ?? .4m))
            || _libraryCandidates.Any(candidate => candidate.Label == tag.Label && candidate.Tags.Length > 0 && candidate.Tags.All(raw => frame.Scores.Any(score => score.Tag == raw && score.Score >= (double)(_threshold.Value ?? .4m))))).ToArray();
        var window = new Window { Title = tag.Label, Width = 500, Height = 420, MinWidth = 380, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Localization.SetIsUserText(window, true);
        var content = new StackPanel { Spacing = 8, Margin = new(20) };
        if(matches.Length == 0) content.Children.Add(Ui.Text(result.Frames.Count == 0 ? "此结果没有视频采样时间" : "没有达到当前阈值的采样画面", "caption"));
        foreach(var frame in matches)
            content.Children.Add(Ui.Button(MediaTime.Format(frame.Seconds) + " · " + Localization.Text("播放画面"), async () =>
            {
                try
                {
                    var file = new FileInfo(result.Path);
                    if(!file.Exists || file.Length != result.Length || file.LastWriteTimeUtc != result.LastWriteUtc) throw new IOException("源文件已改变，请重新分析。");
                    var player = new PlayerWindow(_engine); player.Show(this); await player.OpenAtAsync(result.Path, frame.Seconds); window.Close();
                }
                catch(Exception error) { await Ui.Message(window, "无法播放", error.Message); }
            }));
        window.Content = new ScrollViewer { Content = content }; await window.ShowDialog(this);
    }
}
