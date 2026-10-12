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
        var root = new Grid { RowDefinitions = new("50,*,Auto,Auto"), Margin = new(22, 0, 22, 18), RowSpacing = 4 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(SubmissionHeading(_editing ? "编辑任务 · 自动字幕" : "自动字幕"));
        heading.Children.Add(Ui.ComponentText("每个文件生成一个任务", "caption")); root.Children.Add(heading);
        var columns = new Grid { ColumnDefinitions = new("330,*"), ColumnSpacing = 20 }; _subtitleBody = columns; Grid.SetRow(columns, 1); root.Children.Add(columns);
        var source = new Grid { RowDefinitions = new("Auto,Auto,Auto,164,*"), RowSpacing = 10 };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; label.Children.Add(SubmissionHeading("音视频文件")); label.Children.Add(_count); source.Children.Add(label);
        _sources.MinHeight = 100; _sources.MaxHeight = 220; _sources.Padding = new(0);
        _sources.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _sources.ItemTemplate = new FuncDataTemplate<string>((path, _) => new AvaMedia.Controls.ListRow { Child = new SpeechSourceRow(_engine, path ?? "", _lifetime.Token) });
        _sources.BorderThickness = new(0); _sources.Background = Brushes.Transparent;
        _sources.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = {
            new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        var files = new Grid(); files.Children.Add(_sources); files.Children.Add(_empty);
        var fileCard = Ui.ComponentCard(files); Grid.SetRow(fileCard, 1); source.Children.Add(fileCard);
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
        var previewLabel = Ui.ComponentText(""); previewLabel.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiMediaText"));
        var previewContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var previewIcon = new Controls.LucideIcon { Kind = "play", Width = 12, Height = 12 }; previewIcon.Bind(Controls.LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiMediaText")); previewContent.Children.Add(previewIcon); previewContent.Children.Add(previewLabel);
        var play = new Button { Classes = { "ac" }, Content = previewContent, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left };
        play.Bind(Button.BackgroundProperty, new DynamicResourceExtension("UiMediaOverlay"));
        play.Click += (_, _) => { if (_sources.SelectedItem is string path) new PlayerWindow(_engine, [path]).ShowForPlayback(this); };
        _sources.SelectionChanged += (_, _) => { Localization.SetText(previewLabel, $"预览 · {Path.GetFileName(_sources.SelectedItem as string ?? "")}"); play.IsEnabled = _sources.SelectedItem is string; };
        Avalonia.Automation.AutomationProperties.SetName(play, "打开预览");
        preview.Children.Add(play); var cover = new Border { Child = preview, ClipToBounds = true };
        cover.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("AcThumbRadius"));
        cover.Margin = new(0, 4, 0, 0); Grid.SetRow(cover, 3); source.Children.Add(cover); columns.Children.Add(source);

        var settings = new StackPanel { Spacing = 14 };
        var fields = new StackPanel();
        fields.Children.Add(Field("语言", Choices(_language, SpeechUi.Languages)));
        var models = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8 };
        var values = new[] { SpeechModel.Tiny, SpeechModel.Base, SpeechModel.Small };
        for (var i = 0; i < values.Length; i++)
        {
            var model = values[i]; var index = model switch { SpeechModel.Tiny => 1, SpeechModel.Small => 2, _ => 0 };
            var content = new StackPanel { Spacing = 2 }; var name = SubmissionHeading(SpeechUi.Model(model)); name.TextWrapping = TextWrapping.NoWrap; content.Children.Add(name);
            var detail = Ui.ComponentText($"{model.ToString().ToLowerInvariant()} · {SpeechModelInstaller.Artifact(model).Size / 1_000_000d:0} MB", "caption");
            detail.Opacity = .75; Localization.SetIsUserText(detail, true); content.Children.Add(detail);
            var button = Choice(_model, index, content); button.HorizontalContentAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(button, i); models.Children.Add(button);
        }
        fields.Children.Add(Field("模型", models));
        var output = new StackPanel { Spacing = 6 };
        output.Children.Add(Choices(_output, ["烧录字幕 · MP4", "烧录字幕 · MKV", "字幕文件 · SRT", "样式字幕 · ASS"]));
        output.Children.Add(Ui.ComponentText("先在后台生成字幕草稿，识别完成后「校对字幕」再按所选格式导出", "caption"));
        fields.Children.Add(Field("输出内容", output));
        var folder = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 6 };
        folder.Children.Add(_folder); Grid.SetColumn(_browse, 1); folder.Children.Add(_browse);
        var sourceFolder = new ToggleSwitch { Content = "源目录", IsChecked = _sourceFolder.IsChecked, Classes = { "ac", "small" }, OnContent = null, OffContent = null };
        sourceFolder.IsCheckedChanged += (_, _) => _sourceFolder.IsChecked = sourceFolder.IsChecked;
        Grid.SetColumn(sourceFolder, 2); folder.Children.Add(sourceFolder); fields.Children.Add(Field("保存位置", folder));
        var fieldCard = Ui.ComponentCard(fields); settings.Children.Add(fieldCard);
        var plan = new StackPanel { Spacing = 8 }; plan.Children.Add(SubmissionHeading("运行时将依次"));
        var steps = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 8 };
        var stages = new List<AvaMedia.Controls.StageIndicator>();
        void UpdateSize()
        {
            var model = _model.SelectedIndex switch { 1 => SpeechModel.Tiny, 2 => SpeechModel.Small, _ => SpeechModel.Base };
            var download = Localization.Format($"未安装时下载 {SpeechModelInstaller.Artifact(model).Size / 1_000_000d:0} MB");
            for (var i = 0; i < stages.Count; i++)
                stages[i].Update(i, Localization.Text(SpeechUi.Stages[i]), i == 1 ? download
                    : Localization.Text(i switch { 0 => "检查音频与时长", 2 => "每 5 分钟一段", _ => "字幕草稿" }), "", "waiting");
        }
        _model.SelectionChanged += (_, _) => UpdateSize(); Localization.Changed += UpdatePlanLanguage;
        Closed += (_, _) => Localization.Changed -= UpdatePlanLanguage;
        void UpdatePlanLanguage(object? sender, EventArgs args) => UpdateSize();
        for (var i = 0; i < 4; i++)
        {
            var stage = new AvaMedia.Controls.StageIndicator(); stages.Add(stage);
            Grid.SetColumn(stage, i); steps.Children.Add(stage);
        }
        UpdateSize();
        plan.Children.Add(steps); var planCard = Ui.ComponentCard(plan); settings.Children.Add(planCard);
        var scroll = new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(scroll, 1); columns.Children.Add(scroll);
        Grid.SetRow(_notice, 2); root.Children.Add(_notice);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new(0, 5, 0, 0) };
        footer.Children.Add(Ui.ComponentText("多个文件同时排队，语音识别一次处理一个", "caption"));
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
            button.Classes.Add("ac");
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
        foreach (var field in new Control[] { _language, _model, _output, _sourceFolder })
        { SpeechUi.Detach(field); field.IsVisible = false; root.Children.Add(field); }
        Content = root;
    }

    private static Control Field(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new("96,*"), ColumnSpacing = 12, Margin = new(0, 10) };
        var title = Ui.ComponentText(label, "caption"); title.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(title);
        Grid.SetColumn(content, 1); grid.Children.Add(content);
        var row = new Border { Child = grid };
        if (label != "语言") { row.BorderThickness = new(0, 1, 0, 0); row.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiDivider")); }
        return row;
    }
    private static TextBlock SubmissionHeading(string value) => Ui.ComponentText(value, "heading");

    private static Control Choices(ComboBox value, string[] labels)
    {
        var segments = new AvaMedia.Controls.SegmentedControl { ItemsSource = labels, SelectedIndex = value.SelectedIndex,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            Height = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch };
        segments.SelectionChanged += (_, _) => { if (segments.SelectedIndex >= 0) value.SelectedIndex = segments.SelectedIndex; };
        value.SelectionChanged += (_, _) => segments.SelectedIndex = value.SelectedIndex;
        return segments;
    }
    private static RadioButton Choice(ComboBox value, int index, Control content)
    {
        var button = new RadioButton { Content = content, IsChecked = value.SelectedIndex == index, Classes = { "ac" },
            GroupName = "speech-model-" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.IsCheckedChanged += (_, _) => { if (button.IsChecked == true) value.SelectedIndex = index; };
        value.SelectionChanged += (_, _) => button.IsChecked = value.SelectedIndex == index;
        return button;
    }
}
