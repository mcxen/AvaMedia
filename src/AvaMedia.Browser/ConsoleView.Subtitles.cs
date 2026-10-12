using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private Grid? _subtitleSubmit;
    private readonly ContentControl _subtitleFormHost = new(), _subtitleStartHost = new(), _subtitleSubmitHost = new();
    private readonly ListBox _subtitleFiles = new() { Background = Brushes.Transparent };
    private readonly TextBlock _subtitleFilesEmpty = Ui.Text("请添加服务器文件。", "secondary");
    private bool SubtitleSubmission => !_mobile && _activeFeature == "auto-subtitle" && _workspaceTabs.SelectedIndex == 0;

    private Control BuildSubtitleSubmit()
    {
        _subtitleFiles.Styles.Add(new Style(s => s.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _subtitleFiles.ItemTemplate = new FuncDataTemplate<KeyValuePair<string, FileBrowserItem>>((pair, _) =>
        {
            var titles = new StackPanel { Spacing = 4 };
            titles.Children.Add(Ui.Text(pair.Value.Name, "strong", raw: true, trim: true));
            titles.Children.Add(Ui.Text(pair.Key, "caption", raw: true, trim: true));
            var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12, MinHeight = 56 };
            row.Children.Add(new MediaThumbnail(58, 40) { Path = pair.Key });
            Grid.SetColumn(titles, 1); row.Children.Add(titles);
            var remove = Action("移除", () => { _inputs.Remove(pair.Key); _excludedInputs.Remove(pair.Key); RenderInputs(); return Task.CompletedTask; });
            remove.Height = 28; remove.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(remove, 2); row.Children.Add(remove);
            return new Border { Child = row, BorderBrush = Palette.Hairline, BorderThickness = new(0, 0, 0, 1) };
        });
        var files = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 10 };
        files.Children.Add(Ui.Row(10, Ui.Text("服务器文件", "strong"), Ui.Text("每个文件生成一个任务", "caption")));
        var type = new Border { Background = Palette.Fill, CornerRadius = new(9), Padding = new(12, 10), Child = Ui.Text("任务类型：自动字幕") };
        Grid.SetRow(type, 1); files.Children.Add(type);
        Grid.SetRow(_subtitleFilesEmpty, 2); files.Children.Add(_subtitleFilesEmpty);
        Grid.SetRow(_subtitleFiles, 3); files.Children.Add(_subtitleFiles);
        var fileActions = Actions(Action("添加服务器文件…", PickSubtitleFiles), Action("更换工具", () =>
        {
            _feature.SelectedItem = _features.FirstOrDefault(f => f.Text("id") == "video-compress") ?? _features.FirstOrDefault();
            LayoutCreate(); return Task.CompletedTask;
        }));
        Grid.SetRow(fileActions, 4); files.Children.Add(fileActions);
        var settings = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 12 };
        settings.Children.Add(Ui.Text("识别设置", "strong"));
        Grid.SetRow(_subtitleFormHost, 1); settings.Children.Add(_subtitleFormHost);
        var start = Ui.Line("立即执行", _subtitleStartHost, 44); Grid.SetRow(start, 2); settings.Children.Add(start);
        Grid.SetRow(_subtitleSubmitHost, 3); settings.Children.Add(_subtitleSubmitHost);
        var left = Ui.Card(files, new(18)); var right = Ui.Card(settings, new(18));
        _subtitleSubmit = new Grid { ColumnDefinitions = new("1.9*,1*"), ColumnSpacing = 12, IsVisible = false };
        _subtitleSubmit.Children.Add(left); Grid.SetColumn(right, 1); _subtitleSubmit.Children.Add(right);
        Grid.SetColumnSpan(_subtitleSubmit, 3); return _subtitleSubmit;
    }

    private async Task PickSubtitleFiles()
    {
        if (_pending is not null || _submitting) return;
        var picker = new FileBrowserView(_source, new("服务器文件", Multiple: true, AllowFolders: false,
            StartFolder: _files.CurrentFolder, Embedded: true), L.T, new Glyphs());
        var tab = new TabItem { Header = L.T("服务器文件") };
        void Close() { _workspaceTabs.SelectedIndex = 0; _workspaceTabs.Items.Remove(tab); picker.Dispose(); }
        picker.Selected += items => { AddInputs(items); Close(); };
        picker.Cancelled += Close;
        var content = new Grid { RowDefinitions = new("Auto,*") };
        content.Children.Add(Action("返回字幕设置", () => { Close(); return Task.CompletedTask; }, "chevron-left"));
        Grid.SetRow(picker, 1); content.Children.Add(picker); tab.Content = Ui.Card(content, new(14));
        _workspaceTabs.Items.Add(tab); _workspaceTabs.SelectedItem = tab; await picker.InitializeAsync();
    }

    private void RenderSubtitleInputs()
    {
        _subtitleFiles.ItemsSource = IncludedInputs.ToArray();
        _subtitleFilesEmpty.IsVisible = !IncludedInputs.Any();
        if (_activeFeature == "auto-subtitle")
            _submit.Content = new TextBlock { Text = IncludedInputs.Any(pair => pair.Value.IsFolder) ? L.T("创建任务") : L.F("提交 {0} 个任务", IncludedInputs.Count()), Foreground = Brushes.White };
        else _submit.Content = Ui.Content(L.T("创建任务"), "circle-play", Brushes.White, 13);
    }

    private void BuildSubtitleOptions(JsonNode form)
    {
        _toolOptions.Children.Clear();
        var fields = form["fields"].Items().ToDictionary(f => f.Text("path"));
        var languageNames = new Dictionary<string, string> { ["auto"] = "自动识别", ["zh"] = "中文", ["en"] = "英语", ["ja"] = "日语", ["ko"] = "韩语", ["fr"] = "法语", ["de"] = "德语", ["es"] = "西班牙语", ["ru"] = "俄语" };
        Control Choices(string path, string label, Func<JsonNode, string> caption, bool modelCards = false)
        {
            if (!fields.TryGetValue(path, out var field)) return new StackPanel();
            var choices = field["choices"].Items().ToArray();
            var selected = ReadOption(_optionDraft, path)?.DeepClone() ?? choices.FirstOrDefault()?["value"]?.DeepClone();
            Panel panel = modelCards ? new Grid { ColumnDefinitions = new(string.Join(",", choices.Select(_ => "*"))), ColumnSpacing = 6 } : new WrapStack { Spacing = 6 };
            void Update()
            {
                foreach (var button in panel.Children.OfType<Button>())
                {
                    var active = JsonNode.DeepEquals((JsonNode?)button.Tag, selected);
                    button.Classes.Set("selected", active);
                    if (modelCards && button.Content is StackPanel content)
                        foreach (var text in content.Children.OfType<TextBlock>()) text.Foreground = active ? Brushes.White : (text.Classes.Contains("caption") ? Palette.Secondary : Palette.Text);
                }
            }
            foreach (var choice in choices)
            {
                var button = new Button { Content = caption(choice), Tag = choice["value"]?.DeepClone(), Classes = { "chip" } };
                if (modelCards)
                {
                    var content = new StackPanel { Spacing = 2 };
                    var name = Ui.Text(caption(choice), "strong", raw: true); name.FontSize = 12; name.TextWrapping = TextWrapping.Wrap;
                    content.Children.Add(name);
                    content.Children.Add(Ui.Text(choice["value"]?.ToString() switch { "1" => "tiny", "0" => "base", "2" => "small", _ => "" }, "caption", raw: true));
                    button.Content = content; button.CornerRadius = new(9); button.MinHeight = 62;
                    button.Padding = new(9, 7); button.Classes.Add("outline");
                    button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
                    Grid.SetColumn(button, panel.Children.Count);
                }
                button.Click += (_, _) => { selected = ((JsonNode?)button.Tag)?.DeepClone(); Update(); };
                panel.Children.Add(button);
            }
            Update(); _optionReaders[path] = () => selected?.DeepClone();
            var row = new Grid { ColumnDefinitions = new("96,*"), ColumnSpacing = 12, MinHeight = 42 };
            row.Children.Add(Ui.Text(label, "secondary")); Grid.SetColumn(panel, 1); row.Children.Add(panel);
            _toolOptions.Children.Add(row); _toolOptions.Children.Add(Ui.Hairline()); return panel;
        }
        Choices("transcription.language", "语言", c => L.T(languageNames.GetValueOrDefault(c["value"]?.ToString() ?? "", c.Text("label"))));
        Choices("transcription.model", "模型", c => L.T(c["value"]?.ToString() switch { "1" => "Whisper · 轻量", "0" => "Whisper · 标准", "2" => "Whisper · Small", _ => c.Text("label") }), modelCards: true);
        var recognition = new ToggleSwitch { IsChecked = ReadOption(_optionDraft, "transcription.recognitionOnly")?.GetValue<bool>() == true };
        _optionReaders["transcription.recognitionOnly"] = () => JsonValue.Create(recognition.IsChecked == true);
        var hint = Ui.Text("开启后固定为 SRT，结果保存在任务中", "caption"); hint.TextWrapping = TextWrapping.Wrap;
        var recognitionRow = new Grid { ColumnDefinitions = new("96,*"), ColumnSpacing = 12, MinHeight = 44 };
        recognitionRow.Children.Add(Ui.Text("仅识别字幕结果", "secondary"));
        var recognitionValue = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        recognitionValue.Children.Add(recognition); Grid.SetColumn(hint, 1); recognitionValue.Children.Add(hint);
        Grid.SetColumn(recognitionValue, 1); recognitionRow.Children.Add(recognitionValue); _toolOptions.Children.Add(recognitionRow);
        _toolOptions.Children.Add(Ui.Hairline());
        var format = Choices("format", "格式", c => L.T(c["value"]?.ToString() switch
        { "mp4" => "烧录字幕 · MP4", "mkv" => "烧录字幕 · MKV", "srt" => "字幕文件 · SRT", "ass" => "样式字幕 · ASS", _ => c.Text("label") }));
        var formatReader = _optionReaders.GetValueOrDefault("format");
        _optionReaders["format"] = () => recognition.IsChecked == true ? JsonValue.Create("srt") : formatReader?.Invoke();
        var size = new VisualNumber { Minimum = 0, Maximum = 200, Value = (decimal)(ReadOption(_optionDraft, "subtitleFontSize")?.GetValue<double>() ?? 0) };
        _optionReaders["subtitleFontSize"] = () => JsonValue.Create((double)(size.Value ?? 0));
        _toolOptions.Children.Add(Ui.Line("字幕字号（0 自动）", size, 44));
        void UpdateRecognition()
        {
            format.IsEnabled = size.IsEnabled = recognition.IsChecked != true;
            format.Opacity = size.Opacity = recognition.IsChecked == true ? .45 : 1;
            foreach (var button in ((Panel)format).Children.OfType<Button>())
                button.Classes.Set("selected", JsonNode.DeepEquals((JsonNode?)button.Tag, _optionReaders["format"]()));
        }
        recognition.IsCheckedChanged += (_, _) => UpdateRecognition(); UpdateRecognition();
    }
}
