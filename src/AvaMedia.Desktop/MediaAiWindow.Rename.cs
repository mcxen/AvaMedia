using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Styling;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private sealed class RenameDraft(string path, string[] tags) : Observable
    {
        private string _labels = "", _newName = "", _error = "";
        private bool _include = true;
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        public string[] Tags { get; } = tags;
        public bool Include { get => _include; set => Set(ref _include, value); }
        public string Labels { get => _labels; set => Set(ref _labels, value); }
        public string NewName { get => _newName; set => Set(ref _newName, value); }
        public string Error { get => _error; set => Set(ref _error, value); }
    }
    private async Task OpenRenameDialogAsync()
    {
        if (_busy || !_canRename()) return;
        var selected = _entries.Where(entry => entry.Include).ToArray();
        var rows = selected.Where(entry => _results.ContainsKey(entry.Path))
            .Select(entry => new RenameDraft(entry.Path, ResultTags(_results[entry.Path]).Select(tag => tag.Label).ToArray())).ToArray();
        if (rows.Length == 0) return;
        var window = new Window { Title = "标签重命名", Width = 980, Height = 610, MinWidth = 840, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 12, Margin = new(20) };
        var format = Ui.Combo(["原名 + 标签", "标签 + 序号", "自定义模板"], "原名 + 标签");
        var limit = new NumericUpDown { Minimum = 1, Maximum = 8, Value = 3, Text = "3", Increment = 1, Width = 90 };
        var pattern = Ui.Input("{name}_{keyword}"); Localization.SetIsUserText(pattern, true);
        var custom = new StackPanel { Spacing = 5, IsVisible = false };
        custom.Children.Add(pattern); custom.Children.Add(Ui.Text("可用：{name} 原名、{keyword} 标签、{index} 序号", "caption"));
        var options = new Grid { ColumnDefinitions = new("Auto,250,Auto,Auto,*"), ColumnSpacing = 10 };
        options.Children.Add(Ui.Text("名称格式")); Grid.SetColumn(format, 1); options.Children.Add(format);
        var labelCount = Ui.Text("标签个数"); Grid.SetColumn(labelCount, 2); options.Children.Add(labelCount); Grid.SetColumn(limit, 3); options.Children.Add(limit); root.Children.Add(options);
        Grid.SetRow(custom, 1); root.Children.Add(custom);
        var list = new ListBox { ItemsSource = rows };
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Avalonia.Styling.Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        list.ItemTemplate = new FuncDataTemplate<RenameDraft>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new("28,*,230,*"), ColumnSpacing = 12, Margin = new(0, 7) };
            var check = new CheckBox(); check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(RenameDraft.Include)) { Mode = BindingMode.TwoWay }); grid.Children.Add(check);
            var name = Ui.Text(row.Name); Localization.SetIsUserText(name, true); Grid.SetColumn(name, 1); grid.Children.Add(name);
            var labels = Ui.Input(); labels.Watermark = Localization.Text("命名标签"); Localization.SetIsUserText(labels, true);
            labels.Bind(TextBox.TextProperty, new Binding(nameof(RenameDraft.Labels)) { Mode = BindingMode.TwoWay }); Grid.SetColumn(labels, 2); grid.Children.Add(labels);
            var output = new StackPanel { Spacing = 4 };
            var newName = Ui.Text(""); Localization.SetIsUserText(newName, true); newName.Bind(TextBlock.TextProperty, new Binding(nameof(RenameDraft.NewName))); output.Children.Add(newName);
            var error = Ui.Text("", "error"); error.Bind(TextBlock.TextProperty, new Binding(nameof(RenameDraft.Error))); output.Children.Add(error);
            Grid.SetColumn(output, 3); grid.Children.Add(output); return grid;
        });
        var table = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6 };
        var headings = new Grid { ColumnDefinitions = new("28,*,230,*"), ColumnSpacing = 12, Margin = new(8, 0) };
        foreach (var (text, column) in new[] { ("原文件名", 1), ("命名标签", 2), ("新文件名", 3) })
        { var heading = Ui.Text(text, "caption"); Grid.SetColumn(heading, column); headings.Children.Add(heading); }
        table.Children.Add(headings); Grid.SetRow(list, 1); table.Children.Add(list); Grid.SetRow(table, 2); root.Children.Add(table);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        var status = Ui.Text("", "caption"); footer.Children.Add(status);
        var cancel = Ui.DialogButton("取消", window.Close); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        var apply = new Button { Content = "确认重命名", Classes = { "primary", "dialog-action" } }; Grid.SetColumn(apply, 2); footer.Children.Add(apply);
        Grid.SetRow(footer, 3); root.Children.Add(footer); window.Content = root;
        RenamePreview? preview = null;
        var updating = false;
        void RefreshPreview()
        {
            if (updating) return;
            custom.IsVisible = format.SelectedIndex == 2;
            var template = format.SelectedIndex switch { 0 => "{name}_{keyword}", 1 => "{keyword}_{index}", _ => pattern.Text ?? "" };
            try
            {
                var tagCount = Number(limit);
                if (tagCount != Math.Truncate(tagCount)) throw new ArgumentException("标签个数须为整数。");
                var included = rows.Where(row => row.Include).ToArray();
                preview = BatchRename.PreviewRules(included.Select(row => row.Path), [new(RenameAction.Template) { Text = template }],
                    keywords: included.ToDictionary(row => row.Path, row => row.Labels.Trim(), BatchRename.PathComparer));
                foreach (var row in rows) { row.NewName = ""; row.Error = ""; }
                foreach (var item in preview.Entries)
                {
                    var row = rows.First(row => BatchRename.PathComparer.Equals(row.Path, item.Source)); row.NewName = item.NewName; row.Error = item.Error ?? "";
                }
                status.Text = preview.Errors > 0 ? Localization.Format($"{preview.Errors} 个文件需要修改命名标签") : Localization.Format($"将重命名 {preview.Changes} 个文件");
                if (selected.Length > rows.Length) status.Text += " · " + Localization.Format($"跳过 {selected.Length - rows.Length} 个未分析文件");
                apply.IsEnabled = preview.CanApply;
            }
            catch (Exception error) { preview = null; status.Text = error.Message; apply.IsEnabled = false; }
        }
        void SuggestLabels()
        {
            updating = true;
            foreach (var row in rows)
            {
                var labels = new List<string>();
                foreach (var tag in row.Tags)
                {
                    if (labels.Count >= (int)(limit.Value ?? 3)) break;
                    try { BatchRename.ValidateRenameKeyword(string.Join('_', labels.Append(tag))); labels.Add(tag); }
                    catch (ArgumentException) { }
                }
                row.Labels = string.Join('_', labels);
            }
            updating = false; RefreshPreview();
        }
        foreach (var row in rows) row.PropertyChanged += (_, change) =>
        { if (change.PropertyName is nameof(RenameDraft.Labels) or nameof(RenameDraft.Include)) RefreshPreview(); };
        format.SelectionChanged += (_, _) => RefreshPreview(); pattern.TextChanged += (_, _) => RefreshPreview();
        limit.PropertyChanged += (_, change) =>
        {
            if (change.Property == NumericUpDown.ValueProperty) SuggestLabels();
            else if (change.Property == NumericUpDown.TextProperty) RefreshPreview();
        };
        apply.Click += async (_, _) =>
        {
            RefreshPreview();
            if (preview?.CanApply != true) return;
            try
            {
                foreach (var item in preview.Plan) MediaTagService.ValidateSource(_results[item.Source]);
                BatchRename.ValidateRenamePlan(preview.Plan); window.Close(preview.Plan);
            }
            catch (Exception error) { await Ui.Message(window, "重命名预览失败", error.Message); }
        };
        SuggestLabels();
        var plan = await window.ShowDialog<RenameItem[]?>(this);
        if (!_closed && plan is { Length: > 0 }) await RenameAsync(false, plan);
    }
}
