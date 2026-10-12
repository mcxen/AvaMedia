using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly StackPanel _toolTree = new() { Spacing = 2 };
    private readonly Dictionary<string, Button> _toolChips = new();
    private readonly HashSet<string> _expandedCategories = new() { "视频" };
    private readonly Border _selectedGroup = new() { IsVisible = false }, _rulesGroup = new() { IsVisible = false }, _urlsGroup = new() { IsVisible = false };
    private readonly Border _banner = new() { IsVisible = false };
    private readonly TextBlock _bannerText = Ui.Text("", raw: true);
    private readonly ContentControl _formHostColumn = new(), _formHostEditor = new();
    private readonly ScrollViewer _formScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _summary = Ui.Text("", "secondary", raw: true, trim: true), _toolCaption = Ui.Text("", "caption", raw: true);
    private readonly TextBlock _mobileCount = Ui.Text("", "secondary");
    private Grid _createColumns = null!, _createPage = null!, _desktopBar = null!, _mobileBar = null!, _mobileTop = null!;
    private Border _filesCard = null!, _toolsCard = null!, _outputCard = null!;
    private readonly StackPanel _stepper = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _next;
    private int _createStep;

    private void BuildForm()
    {
        _selectedGroup.Child = new StackPanel { Children = { Header(_selection, Action("清空", () => { _inputs.Clear(); _excludedInputs.Clear(); _rotationOverrides.Clear(); RenderInputs(); return Task.CompletedTask; })),
            new Border { Background = Brushes.White, CornerRadius = new(10), BorderBrush = Palette.Hairline, BorderThickness = new(1), Child = new ScrollViewer { Content = _selected, MaxHeight = 170 } } }, Spacing = 6 };
        var urls = new StackPanel { Spacing = 6 }; Field(urls, "下载链接（每行一个）", _urls); _urlsGroup.Child = urls;
        var rules = new StackPanel { Spacing = 6 }; rules.Children.Add(Ui.Text("分类规则", "section")); rules.Children.Add(new ScrollViewer { Content = _rules, MaxHeight = 220 });
        _rulesGroup.Child = rules;
        var output = new StackPanel { Spacing = 8 };
        output.Children.Add(Ui.Text("服务器输出目录", "section"));
        var path = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        path.Children.Add(_output); var folder = Ui.Icon("folder-closed", 14, Palette.Secondary); Grid.SetColumn(folder, 1); path.Children.Add(folder);
        output.Children.Add(new Border { Child = path, Background = Brushes.White, BorderBrush = Palette.Hairline, BorderThickness = new(1), CornerRadius = new(7), Padding = new(9, 6) });
        output.Children.Add(Actions(Action("选择输出文件夹…", () => PickServerFile("输出目录", true, selected => { _output.Text = selected; UpdateSummary(); })),
            Action("使用当前目录输出", () => { if (_files.CurrentFolder is { } current) { _output.Text = _source.DisplayAddress(current); UpdateSummary(); } return Task.CompletedTask; })));
        _mobileExtras.Child = Ui.Group(Ui.Line("立即执行", _formStartHost, 44), Ui.Line("包括子文件夹", _recursiveHostMobile, 44));
        _form.Children.Add(_selectedGroup); _form.Children.Add(_urlsGroup);
        _form.Children.Add(_toolOptions); _form.Children.Add(_analysis); _form.Children.Add(_rulesGroup); _form.Children.Add(_downloadModels);
        _form.Children.Add(output); _form.Children.Add(_mobileExtras);
        _formScroll.Content = new Border { Child = _form, Padding = new(14, 4, 14, 14) };
    }
    private static Grid Header(Control left, Control? right = null)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        grid.Children.Add(left); if (right is not null) { Grid.SetColumn(right, 1); grid.Children.Add(right); }
        return grid;
    }
    private static Control CardHeader(int number, string title, Control? caption)
    {
        var badge = new Border { Width = 20, Height = 20, CornerRadius = new(10), Background = Palette.Blue, Child = new TextBlock
        { Text = number.ToString(), Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var text = Ui.Text(title, "strong"); text.FontSize = 14.5;
        return new Border { Padding = new(14, 12, 14, 8), Child = Header(Ui.Row(8, badge, text), caption) };
    }

    private Control BuildCreateColumns()
    {
        var filesBody = new Grid { RowDefinitions = new("Auto,*") };
        filesBody.Children.Add(CardHeader(1, "选择文件", Ui.Text("服务器文件", "caption")));
        Grid.SetRow(_files, 1); filesBody.Children.Add(_files);
        _filesCard = Ui.Card(filesBody);

        var toolsBody = new Grid { RowDefinitions = new("Auto,*") };
        toolsBody.Children.Add(CardHeader(2, "选择工具", Ui.Text("工具分类 · 任务类型", "caption")));
        var treeScroll = new ScrollViewer { Content = new Border { Child = _toolTree, Padding = new(8, 0, 10, 12) }, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(treeScroll, 1); toolsBody.Children.Add(treeScroll);
        _toolsCard = Ui.Card(toolsBody);

        var outputBody = new Grid { RowDefinitions = new("Auto,*") };
        outputBody.Children.Add(CardHeader(3, "输出与提交", _toolCaption));
        Grid.SetRow(_formHostColumn, 1); outputBody.Children.Add(_formHostColumn);
        _outputCard = Ui.Card(outputBody);

        _createColumns = new Grid { ColumnDefinitions = new("1.35*,1*,1*"), ColumnSpacing = 12 };
        _createColumns.Children.Add(_filesCard);
        Grid.SetColumn(_toolsCard, 1); _createColumns.Children.Add(_toolsCard);
        Grid.SetColumn(_outputCard, 2); _createColumns.Children.Add(_outputCard);
        _createColumns.Children.Add(BuildSubtitleSubmit());
        PlaceForm();
        return _createColumns;
    }

    private Control BuildCreatePage()
    {
        _createPage = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 12, Margin = new(18, 2, 18, 18) };
        // Restore banner
        var bannerIcon = Ui.Icon("circle-alert", 15, Palette.Orange); _bannerText.Foreground = Palette.BannerText; _bannerText.FontSize = 12.5;
        _bannerText.TextWrapping = TextWrapping.Wrap;
        var bannerGrid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
        bannerGrid.Children.Add(bannerIcon); Grid.SetColumn(_bannerText, 1); bannerGrid.Children.Add(_bannerText);
        _discard.Classes.Add("outline");
        var bannerActions = Ui.Row(8, _discard, _recover); Grid.SetColumn(bannerActions, 2); bannerGrid.Children.Add(bannerActions);
        _banner.Child = bannerGrid; _banner.Background = Palette.Banner; _banner.CornerRadius = new(12); _banner.Padding = new(14, 9);
        _banner.BorderBrush = new SolidColorBrush(Color.Parse("#F5DFAE")); _banner.BorderThickness = new(1);
        // Mobile top bar + stepper
        _mobileTop = new Grid { RowDefinitions = new("Auto,Auto"), RowSpacing = 10, Margin = new(0, 6, 0, 0) };
        var back = new Button { Content = Ui.Icon("chevron-left", 22, Palette.Blue), Classes = { "plain", "icon" } };
        back.Click += (_, _) => { if (_workspaceTabs.SelectedIndex != 0) _workspaceTabs.SelectedIndex = 0; else if (_createStep > 0) { _createStep--; LayoutCreate(); } else _tabs.SelectedIndex = 1; };
        var title = Ui.Text("新建任务", "strong"); title.FontSize = 17; title.HorizontalAlignment = HorizontalAlignment.Center;
        var topRow = new Grid { ColumnDefinitions = new("*,Auto,*") };
        topRow.Children.Add(back); Grid.SetColumn(title, 1); topRow.Children.Add(title);
        var pill = Ui.Pill(Ui.Dot(Palette.Green, "已连接", Palette.GreenText), Palette.GreenSoft); pill.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(pill, 2); topRow.Children.Add(pill);
        _mobileTop.Children.Add(topRow); Grid.SetRow(_stepper, 1); _mobileTop.Children.Add(_stepper);
        _createPage.Children.Add(_mobileTop);
        Grid.SetRow(_banner, 1); _createPage.Children.Add(_banner);
        Grid.SetRow(_workspaceTabs, 2); _createPage.Children.Add(_workspaceTabs);
        // Desktop action bar
        _desktopBar = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 22, Margin = new(16, 10) };
        _desktopBar.Children.Add(_recursiveHostDesktop);
        Grid.SetColumn(_startHostDesktop, 1); _desktopBar.Children.Add(_startHostDesktop);
        _summary.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(_summary, 2); _desktopBar.Children.Add(_summary);
        Grid.SetColumn(_submitHostDesktop, 3); _desktopBar.Children.Add(_submitHostDesktop);
        // Mobile bottom bar
        _mobileBar = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 14, Margin = new(16, 12) };
        _mobileBar.Children.Add(_mobileCount);
        Grid.SetColumn(_submitHostMobile, 1); _mobileBar.Children.Add(_submitHostMobile);
        var bar = new Panel(); bar.Children.Add(_desktopCardBar = Ui.Card(_desktopBar)); bar.Children.Add(_mobileBarBorder = new Border
        { Child = _mobileBar, Background = new SolidColorBrush(Color.FromArgb(245, 249, 249, 251)), BorderBrush = Palette.Hairline, BorderThickness = new(0, 1, 0, 0), Margin = new(-18, 0, -18, -18) });
        Grid.SetRow(bar, 3); _createPage.Children.Add(bar);
        _start.IsCheckedChanged += (_, _) => UpdateSummary();
        _files.CompactSelectionChanged += (count, _) => { if (_mobile) _mobileCount.Text = L.F("已勾选 {0} 项", count); };
        LayoutCreate();
        return _createPage;
    }
    private readonly ContentControl _startHostDesktop = new(), _submitHostDesktop = new(), _submitHostMobile = new();
    private Border _desktopCardBar = null!, _mobileBarBorder = null!;

    /// <summary>Desktop: three columns. Mobile: one step at a time with a stepper and a full-width action.</summary>
    private void LayoutCreate()
    {
        if (_createColumns is null || _createPage is null || _desktopBar is null) return;
        _subtitleFormHost.Content = null; _subtitleStartHost.Content = null; _subtitleSubmitHost.Content = null; _formStartHost.Content = null;
        _mobileTop.IsVisible = _mobile; _desktopCardBar.IsVisible = !_mobile; _mobileBarBorder.IsVisible = _mobile;
        _createPage.Margin = _mobile ? new(16, 0, 16, 18) : new(18, 2, 18, 18);
        _startHostDesktop.Content = null; _submitHostDesktop.Content = null; _submitHostMobile.Content = null;
        if (!_mobile)
        {
            _createColumns.ColumnDefinitions = new("1.35*,1*,1*"); _createColumns.ColumnSpacing = 12;
            foreach (var (card, column) in new[] { (_filesCard, 0), (_toolsCard, 1), (_outputCard, 2) }) { card.IsVisible = true; Grid.SetColumn(card, column); }
            _startHostDesktop.Content = _start; _submitHostDesktop.Content = _submit; _submit.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            _createColumns.ColumnDefinitions = new("*"); _createColumns.ColumnSpacing = 0;
            var editing = _workspaceTabs.SelectedIndex != 0;
            _filesCard.IsVisible = _createStep == 0; _toolsCard.IsVisible = _createStep == 1; _outputCard.IsVisible = _createStep == 2;
            foreach (var card in new[] { _filesCard, _toolsCard, _outputCard }) Grid.SetColumn(card, 0);
            if (_createStep == 2 || editing) { _submitHostMobile.Content = _submit; _submit.HorizontalAlignment = HorizontalAlignment.Stretch; _mobileCount.IsVisible = false; }
            else { _submitHostMobile.Content = _nextButton; _mobileCount.IsVisible = true; }
            _nextButton.Content = Ui.Content(L.T(_createStep == 0 ? "下一步：选择工具" : "下一步：输出") + "  ›", null);
            ((Control)_nextButton.Content).SetValue(TextBlock.ForegroundProperty, Brushes.White);
            _mobileCount.Text = _createStep == 0 ? L.F("已勾选 {0} 项", _inputs.Count) : (_feature.SelectedItem as JsonNode).Text("name");
            RenderStepper();
        }
        // Mobile keeps the start toggle and subfolder option inside the output step.
        _mobileExtras.IsVisible = _mobile;
        _recursiveHostDesktop.Content = null; _recursiveHostMobile.Content = null; _formStartHost.Content = null;
        if (_mobile) { _start.Content = null; _formStartHost.Content = _start; _recursive.Content = null; _recursiveHostMobile.Content = _recursive; }
        else { _start.Content = L.T("立即执行"); _startHostDesktop.Content = _start; _recursive.Content = L.T("包括子文件夹"); _recursiveHostDesktop.Content = _recursive; }
        _createPage.RowSpacing = SubtitleSubmission && !_banner.IsVisible ? 0 : 12;
        if (_subtitleSubmit is not null) _subtitleSubmit.IsVisible = SubtitleSubmission;
        if (_formScroll.Content is Border formPadding)
            formPadding.Padding = SubtitleSubmission ? new(0) : new(14, 4, 14, 14);
        _formHostColumn.Content = null; _formHostEditor.Content = null;
        if (SubtitleSubmission)
        {
            _filesCard.IsVisible = _toolsCard.IsVisible = _outputCard.IsVisible = false;
            _desktopCardBar.IsVisible = false;
            _startHostDesktop.Content = null; _submitHostDesktop.Content = null;
            _start.Content = null; _subtitleStartHost.Content = _start; _subtitleSubmitHost.Content = _submit;
            _submit.HorizontalAlignment = HorizontalAlignment.Stretch;
            _subtitleFormHost.Content = _formScroll;
        }
        else if (_workspaceTabs.SelectedIndex == 1) _formHostEditor.Content = _formScroll;
        else _formHostColumn.Content = _formScroll;
        _selectedGroup.IsVisible = _inputs.Count > 0 && _workspaceTabs.SelectedIndex != 1 && !SubtitleSubmission;
        RenderSubtitleInputs();
    }
    private readonly ContentControl _formStartHost = new(), _recursiveHostMobile = new(), _recursiveHostDesktop = new();
    private Border _mobileExtras = new() { IsVisible = false };
    private Button? _nextButtonField;
    private Button _nextButton => _nextButtonField ??= MakeNext();
    private Button MakeNext()
    {
        var button = new Button { Classes = { "primary", "large" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) =>
        {
            if (_createStep == 0 && _files.AddButton.IsEnabled) _files.AddButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            _createStep = Math.Min(2, _createStep + 1); LayoutCreate();
        };
        return button;
    }
    private void RenderStepper()
    {
        _stepper.Children.Clear();
        var labels = new[] { "1. 选择文件", "2. 选择工具", "3. 输出" };
        for (var index = 0; index < labels.Length; index++)
        {
            var step = index; var done = index < _createStep; var active = index == _createStep;
            var content = done ? Ui.Content(L.T(labels[index]), "check", Palette.Blue, 13) : (Control)Ui.Text(L.T(labels[index]));
            var button = new Button { Content = content, MinWidth = 104, Classes = { active ? "primary" : "outline" } };
            if (done && content is StackPanel panel) foreach (var text in panel.Children.OfType<TextBlock>()) text.Foreground = Palette.Blue;
            if (!done && !active && content is TextBlock muted) muted.Foreground = Palette.Secondary;
            button.Click += (_, _) => { _createStep = step; LayoutCreate(); };
            _stepper.Children.Add(button);
        }
    }

    /// <summary>The option form lives in column 3, or in the editor side panel while editing.</summary>
    private void PlaceForm()
    {
        _subtitleFormHost.Content = null;
        _formHostColumn.Content = null; _formHostEditor.Content = null;
        if (_workspaceTabs.SelectedIndex == 1) _formHostEditor.Content = _formScroll; else _formHostColumn.Content = _formScroll;
        // The editor shows files as chips; the side panel keeps only the tool options.
        _selectedGroup.IsVisible = _inputs.Count > 0 && _workspaceTabs.SelectedIndex != 1;
        if (_openEditor is not null) _openEditor.IsVisible = _workspaceTabs.SelectedIndex != 1;
        if (_createColumns is not null) LayoutCreate();
        UpdateEditorChrome();
    }

    // ---------- Tool tree ----------
    private static readonly (string Category, string Icon)[] CategoryIcons = [("视频", "video"), ("音频", "audio-lines"), ("图片", "image-file"), ("文档", "file-text"), ("工具集", "sliders-horizontal"), ("光盘", "disc")];
    private static string Section(JsonNode feature)
    {
        var id = feature.Text("id"); var category = feature.Text("category"); var format = feature.Text("format");
        return category switch
        {
            "视频" => id switch
            {
                "player" or "download" or "info" => "播放",
                "mp4" or "video-compress" or "video-slim" or "repair" => "格式与体积",
                "clip" or "person-clip" or "delogo" or "frames" => "剪辑",
                "split" or "extract-video" or "join" or "mux" => "音视频轨道",
                _ => "字幕与内容"
            },
            "音频" => id == "audio-" + format ? "转换为" : "音频",
            "图片" => id == "image-" + format ? "转换为" : "图片",
            "文档" => id is "pdf-merge" or "pdf-split" or "pdf-age" or "pdf-compress" ? "文档" : "格式转换",
            "工具集" => id switch { "zip" or "unzip" => "压缩与解压", "crop" or "rotate" or "contact-sheet" => "批量画面", _ => "媒体整理" },
            _ => category
        };
    }
    private static readonly string[] VideoOrder = ["player", "download", "info", "mp4", "video-compress", "video-slim", "repair", "clip", "person-clip", "delogo", "frames", "split", "extract-video", "join", "mux"];
    private void RenderToolTree()
    {
        _toolTree.Children.Clear(); _toolChips.Clear();
        var categories = _features.Select(item => item.Text("category")).Distinct().OrderBy(name => Array.FindIndex(CategoryIcons, pair => pair.Category == name) is var index and >= 0 ? index : 99).ToArray();
        foreach (var category in categories)
        {
            var items = _features.Where(item => item.Text("category") == category).ToArray();
            var expanded = _expandedCategories.Contains(category);
            var icon = CategoryIcons.FirstOrDefault(pair => pair.Category == category).Icon ?? "layers";
            var header = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 8, Height = 30, Margin = new(4, 0) };
            header.Children.Add(Ui.Icon(expanded ? "chevron-down" : "chevron-right", 13, Palette.Secondary));
            var categoryIcon = Ui.Icon(icon, 15, Palette.Blue); Grid.SetColumn(categoryIcon, 1); header.Children.Add(categoryIcon);
            var name = Ui.Text(category, "strong"); name.FontSize = 13.5; Grid.SetColumn(name, 2); header.Children.Add(name);
            var count = Ui.Text(items.Length.ToString(), "caption"); Grid.SetColumn(count, 3); header.Children.Add(count);
            var toggle = new Button { Content = header, Classes = { "row" }, CornerRadius = new(8) };
            toggle.Click += (_, _) => { if (!_expandedCategories.Remove(category)) { _expandedCategories.Clear(); _expandedCategories.Add(category); } RenderToolTree(); };
            AutomationProperties.SetName(toggle, category); _toolTree.Children.Add(toggle);
            if (!expanded) continue;
            var body = new StackPanel { Spacing = 6, Margin = new(30, 2, 0, 8) };
            foreach (var section in items.GroupBy(Section))
            {
                body.Children.Add(Ui.Text(section.Key, "caption"));
                var chips = new WrapStack { Spacing = 6 };
                foreach (var feature in section.OrderBy(item => Array.IndexOf(VideoOrder, item.Text("id")) is var index and >= 0 ? index : 99))
                {
                    var chip = new Button { Content = feature.Text("name"), Classes = { "chip" } };
                    var id = feature.Text("id"); _toolChips[id] = chip;
                    chip.Click += (_, _) => { _feature.SelectedItem = feature; if (_mobile) { _createStep = 2; LayoutCreate(); } };
                    chips.Children.Add(chip);
                }
                body.Children.Add(chips);
            }
            _toolTree.Children.Add(body);
        }
        RenderToolSelection();
    }
    private string _revealedTool = "";
    private void RenderToolSelection()
    {
        var id = (_feature.SelectedItem as JsonNode).Text("id");
        foreach (var (key, chip) in _toolChips) chip.Classes.Set("selected", key == id);
        _toolCaption.Text = (_feature.SelectedItem as JsonNode).Text("name");
        // Reveal the category of a newly selected tool once; afterwards the user's accordion choice wins.
        if (_feature.SelectedItem is JsonNode feature && id != _revealedTool && _toolChips.Count > 0)
        {
            _revealedTool = id;
            if (!_expandedCategories.Contains(feature.Text("category"))) { _expandedCategories.Clear(); _expandedCategories.Add(feature.Text("category")); RenderToolTree(); }
        }
    }

    // ---------- Summary ----------
    private string SummaryText()
    {
        var files = IncludedInputs.Count();
        var tool = (_feature.SelectedItem as JsonNode).Text("name");
        var output = PathInput.ShortName(_output.Text ?? "");
        return L.F("{0} 个文件 · {1} · 输出到 {2}", files, tool, output);
    }
    private void UpdateSummary()
    {
        _summary.Text = SummaryText(); ToolTip.SetTip(_summary, _output.Text);
        if (_mobile && _createStep == 0) _mobileCount.Text = L.F("已添加 {0} 项", _inputs.Count);
        UpdateEditorSummary();
    }
}
