using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using AvaMedia.Core;
using AvaMedia.Desktop.AiProgress;

namespace AvaMedia.Desktop;

public sealed partial class SpeechToolsWindow
{
    private bool _startSpeechImmediately = true;
    private Button? _queueSpeech;
    private Grid? _subtitleBody;

    private void BuildSubtitleSubmission(Func<Task>? pick)
    {
        SpeechUi.ApplyWindowStyle(this);
        Width = 940; Height = 560; MinWidth = 940; MinHeight = 560;
        foreach (var control in new Control[] { _sources, _empty, _count, _remove, _preview, _folder, _browse, _sourceFolder, _confirm, _notice }) SpeechUi.Detach(control);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(22, 12, 22, 18), RowSpacing = 12 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        heading.Children.Add(SpeechUi.Heading(_editing ? "编辑任务 · 自动字幕" : "自动字幕"));
        heading.Children.Add(SpeechUi.Text("每个文件生成一个任务", true)); root.Children.Add(heading);
        var columns = new Grid { ColumnDefinitions = new("330,*"), ColumnSpacing = 20 }; _subtitleBody = columns; Grid.SetRow(columns, 1); root.Children.Add(columns);
        var source = new Grid { RowDefinitions = new("Auto,*,Auto,160"), RowSpacing = 10 };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; label.Children.Add(SpeechUi.Heading("音视频文件")); label.Children.Add(_count); source.Children.Add(label);
        _sources.MinHeight = 100; _sources.MaxHeight = double.PositiveInfinity;
        _sources.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _sources.ItemTemplate = new FuncDataTemplate<string>((path, _) => new SpeechSourceRow(_engine, path ?? "", _lifetime.Token));
        _sources.BorderThickness = new(0); _sources.Background = Brushes.Transparent;
        _sources.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = {
            new Setter(PaddingProperty, new Thickness(8)), new Setter(MinHeightProperty, 56d),
            new Setter(CornerRadiusProperty, new CornerRadius(9)), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        var files = new Grid(); files.Children.Add(_sources); files.Children.Add(_empty);
        var fileCard = SpeechUi.Card(files, 6); Grid.SetRow(fileCard, 1); source.Children.Add(fileCard);
        var fileActions = new WrapPanel(); var choose = Ui.Button("选择音视频文件…", async () => { if (!_busy && pick is not null) await pick(); });
        choose.Margin = new(0, 0, 6, 0); fileActions.Children.Add(choose); fileActions.Children.Add(_remove); Grid.SetRow(fileActions, 2); source.Children.Add(fileActions);
        // The submit window uses a single cover with an overlaid preview action.
        // Seeking remains available in the full player opened by this action.
        if (_preview.Content is Grid media)
        {
            media.RowSpacing = 0;
            foreach (var slider in media.Children.OfType<Slider>().ToArray()) media.Children.Remove(slider);
        }
        var preview = new Grid(); preview.Children.Add(_preview);
        var previewLabel = SpeechUi.Text("", size: 12); previewLabel.Foreground = Brushes.White;
        var previewContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        previewContent.Children.Add(new Controls.LucideIcon { Kind = "play", Width = 12, Height = 12, Foreground = Brushes.White }); previewContent.Children.Add(previewLabel);
        var play = new Button { Content = previewContent, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(12, 10), CornerRadius = new(0), BorderThickness = new(0),
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Color.Parse("#88000000"), 1) } } };
        play.Click += (_, _) => { if (_sources.SelectedItem is string path) new PlayerWindow(_engine, [path]).ShowForPlayback(this); };
        _sources.SelectionChanged += (_, _) => { Localization.SetText(previewLabel, $"预览 · {Path.GetFileName(_sources.SelectedItem as string ?? "")}"); play.IsEnabled = _sources.SelectedItem is string; };
        Avalonia.Automation.AutomationProperties.SetName(play, "打开预览");
        preview.Children.Add(play); var cover = new Border { Child = preview, CornerRadius = new(12), ClipToBounds = true };
        Grid.SetRow(cover, 3); source.Children.Add(cover); columns.Children.Add(source);

        var settings = new StackPanel { Spacing = 14 };
        var fields = new StackPanel();
        fields.Children.Add(Field("语言", Choices(_language, SpeechUi.Languages)));
        var models = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8 };
        var values = new[] { SpeechModel.Tiny, SpeechModel.Base, SpeechModel.Small };
        for (var i = 0; i < values.Length; i++)
        {
            var model = values[i]; var index = model switch { SpeechModel.Tiny => 1, SpeechModel.Small => 2, _ => 0 };
            var content = new StackPanel { Spacing = 2 }; var name = SpeechUi.Heading(SpeechUi.Model(model)); name.FontSize = 12.5; name.TextWrapping = TextWrapping.NoWrap; content.Children.Add(name);
            var detail = SpeechUi.Text($"{model.ToString().ToLowerInvariant()} · {SpeechModelInstaller.Artifact(model).Size / 1_000_000d:0} MB", size: 11);
            detail.Opacity = .75; Localization.SetIsUserText(detail, true); content.Children.Add(detail);
            var button = Choice(_model, index, content); button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new(12, 7);
            Grid.SetColumn(button, i); models.Children.Add(button);
        }
        fields.Children.Add(Field("模型", models));
        var output = new StackPanel { Spacing = 6 };
        output.Children.Add(Choices(_output, ["烧录字幕 · MP4", "烧录字幕 · MKV", "字幕文件 · SRT", "样式字幕 · ASS"]));
        output.Children.Add(SpeechUi.Text("先在后台生成字幕草稿，识别完成后「校对字幕」再按所选格式导出", true, 11));
        fields.Children.Add(Field("输出内容", output));
        var folder = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 6 };
        folder.Children.Add(_folder); Grid.SetColumn(_browse, 1); folder.Children.Add(_browse);
        var sourceFolder = new ToggleButton { Content = "源目录", IsChecked = _sourceFolder.IsChecked, Padding = new(12, 5), CornerRadius = new(16) };
        SpeechUi.PreserveChoiceFace(sourceFolder);
        void UpdateSourceFolder() => SpeechUi.SetChoiceState(sourceFolder);
        sourceFolder.IsCheckedChanged += (_, _) => UpdateSourceFolder(); UpdateSourceFolder();
        sourceFolder.IsCheckedChanged += (_, _) => _sourceFolder.IsChecked = sourceFolder.IsChecked;
        Grid.SetColumn(sourceFolder, 2); folder.Children.Add(sourceFolder); fields.Children.Add(Field("保存位置", folder));
        var fieldCard = SpeechUi.Card(fields, 0); fieldCard.Padding = new(16, 4); settings.Children.Add(fieldCard);
        var plan = new StackPanel { Spacing = 10 }; plan.Children.Add(SpeechUi.Heading("运行时将依次"));
        var steps = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 8 };
        var download = SpeechUi.Text("", true, 10.5);
        void UpdateSize() => download.Text = Localization.Format($"未安装时下载 {SpeechModelInstaller.Artifact(_model.SelectedIndex switch { 1 => SpeechModel.Tiny, 2 => SpeechModel.Small, _ => SpeechModel.Base }).Size / 1_000_000d:0} MB");
        _model.SelectionChanged += (_, _) => UpdateSize(); Localization.Changed += UpdatePlanLanguage;
        Closed += (_, _) => Localization.Changed -= UpdatePlanLanguage;
        void UpdatePlanLanguage(object? sender, EventArgs args) => UpdateSize();
        UpdateSize();
        for (var i = 0; i < 4; i++)
        {
            var step = new StackPanel { Spacing = 3 }; step.Children.Add(SpeechUi.Text((i + 1).ToString(), true, 10.5)); var title = SpeechUi.Heading(SpeechUi.Stages[i]); title.FontSize = 12; step.Children.Add(title);
            step.Children.Add(i == 1 ? download : SpeechUi.Text(i switch { 0 => "检查音频与时长", 2 => "每 5 分钟一段", _ => "字幕草稿" }, true, 10.5));
            var card = SpeechUi.Card(step, 8); card.CornerRadius = new(9); card.BorderThickness = new(0); card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSpeechStepSurface"));
            Grid.SetColumn(card, i); steps.Children.Add(card);
        }
        plan.Children.Add(steps); settings.Children.Add(SpeechUi.Card(plan));
        var scroll = new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(scroll, 1); columns.Children.Add(scroll);
        Grid.SetRow(_notice, 2); root.Children.Add(_notice);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(SpeechUi.Text("多个文件同时排队，语音识别一次处理一个", true, 11.5));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Ui.Button("取消", () => Close(null)));
        _queueSpeech = Ui.Button("加入队列", () =>
        {
            _startSpeechImmediately = false;
            try { _confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { _startSpeechImmediately = true; }
        });
        _queueSpeech.IsVisible = !_editing; actions.Children.Add(_queueSpeech); actions.Children.Add(_confirm);
        foreach (var button in actions.Children.OfType<Button>().Concat(new[] { choose, _remove, _browse }))
            SpeechUi.CompactButton(button, button == _confirm ? 32 : 28);
        _confirm.Padding = new(18, 5); _folder.Height = 28; _folder.MinHeight = 0; _folder.FontSize = 12;
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
        foreach (var field in new Control[] { _language, _model, _output, _sourceFolder })
        { SpeechUi.Detach(field); field.IsVisible = false; root.Children.Add(field); }
        Content = root;
    }

    private static Control Field(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new("96,*"), ColumnSpacing = 12, Margin = new(0, 7) };
        var title = SpeechUi.Text(label, true); title.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(title);
        Grid.SetColumn(content, 1); grid.Children.Add(content);
        var row = new Border { Child = grid };
        if (label != "语言") { row.BorderThickness = new(0, 1, 0, 0); row.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiDivider")); }
        return row;
    }
    private static Control Choices(ComboBox value, string[] labels)
    {
        var wrap = new WrapPanel();
        for (var i = 0; i < labels.Length; i++) { var button = Choice(value, i, SpeechUi.Text(labels[i])); button.Margin = new(0, 0, 6, 3); wrap.Children.Add(button); }
        return wrap;
    }
    private static ToggleButton Choice(ComboBox value, int index, Control content)
    {
        var button = new ToggleButton { Content = content, IsChecked = value.SelectedIndex == index, Padding = new(10, 3), CornerRadius = new(9), BorderThickness = new(1),
            MinHeight = 0, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        SpeechUi.PreserveChoiceFace(button);
        var labels = content.GetLogicalDescendants().OfType<TextBlock>().Prepend(content as TextBlock).OfType<TextBlock>().ToArray();
        void UpdateFace() => SpeechUi.SetChoiceState(button, labels);
        button.IsCheckedChanged += (_, _) => UpdateFace(); UpdateFace();
        button.Click += (_, _) => { value.SelectedIndex = index; button.IsChecked = true; };
        value.SelectionChanged += (_, _) => button.IsChecked = value.SelectedIndex == index;
        return button;
    }
}
