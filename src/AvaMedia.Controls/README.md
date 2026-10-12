# AvaMedia.Controls

独立的 Avalonia 11.3.22 通用组件库，不引用 AvaMedia 的业务、桌面或媒体项目。保留宿主的 Themes.Simple；现代组件通过角色类名与具名 ControlTheme 接入，经典皮肤保留宿主原生控件模板。所有颜色、尺寸和圆角取自主题资源。

结构可独立打包：`dotnet pack AvaMedia.Controls.csproj`。项目自身声明依赖和包元数据，离开本仓库也能构建。许可证沿用项目的 AGPL-3.0-only。

实现自行编写；Devolutions.AvaloniaTheme.MacOS 仅作为评估参考，未复制其源码。最后兼容 Avalonia 11 的稳定版本是 2026.6.16；不引入 Avalonia 12 的依赖或整体主题。

## 接入

在 `SimpleTheme` 之后、宿主调色板之前加载：

```xml
<StyleInclude Source="avares://AvaMedia.Controls/Themes/Components.axaml" />
```

普通控件添加 `ac` 类，按钮再添加 `primary`、`secondary`、`borderless` 或 `danger`。具名主题为 `AcButton`、`AcCheckBox`、`AcRadioButton`、`AcToggleSwitch`、`AcComboBox`、`AcNumericUpDown`、`AcTextBox`、`AcSlider`、`AcProgressBar`、`AcToolTip`、`AcScrollBar`。主题不覆盖 Avalonia 类型资源键，选择器排除宿主经典窗口类 `mac-os9` / `windows-xp`。

组合类型：`Card`、`ListRow`、`SectionHeader`、`StatusPill`、`StageIndicator`、`SubStepRow`、`StatusGlyph`、`SegmentedControl`、`EmptyState`。状态取 `running`、`done` / `completed`、`paused`、`failed`、`waiting` / `cancelled`。它们只接收显示内容，不了解媒体任务或应用皮肤类型。

`Themes/Defaults.axaml` 提供浅色与深色默认值。宿主覆盖 `Ui*` 颜色及 `Ac*` 尺寸和表面令牌，组合组件使用动态资源，已有实例也会响应皮肤切换。具体契约及 macOS 参考尺寸见仓库 UISPEC.MD 的 7.9 节。经典皮肤的标准控件继续由宿主主题提供；组合控件的圆角和阴影由同名 `Ac*` 令牌覆盖。
