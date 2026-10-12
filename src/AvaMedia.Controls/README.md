# AvaMedia.Controls

独立的 Avalonia 11.3.22 通用组件库，不引用 AvaMedia 的业务、桌面或媒体项目。保留宿主的 Themes.Simple；现代组件通过角色类名与具名 ControlTheme 接入，经典皮肤保留宿主原生控件模板。所有颜色、尺寸和圆角取自主题资源。

结构可独立打包：`dotnet pack AvaMedia.Controls.csproj`。项目自身声明依赖和包元数据，离开本仓库也能构建。许可证沿用项目的 AGPL-3.0-only。

实现自行编写；Devolutions.AvaloniaTheme.MacOS 仅作为评估参考，未复制其源码。最后兼容 Avalonia 11 的稳定版本是 2026.6.16；不引入 Avalonia 12 的依赖或整体主题。

## 接入

在 `SimpleTheme` 之后、宿主调色板之前加载：

```xml
<StyleInclude Source="avares://AvaMedia.Controls/Themes/Components.axaml" />
```

普通控件添加 `ac` 类，按钮再添加 `primary`、`secondary`、`borderless` 或 `danger`。具名主题为 `AcButton`、`AcCheckBox`、`AcRadioButton`、`AcToggleSwitch`、`AcComboBox`、`AcNumericUpDown`、`AcTextBox`、`AcSlider`、`AcProgressBar`、`AcToolTip`、`AcScrollBar`、`AcToggleButton`、`AcExpander`、`AcTreeView/AcTreeViewItem`、`AcTabControl/AcTabItem`、`AcListBox/AcListBoxItem`。主题不覆盖 Avalonia 类型资源键，选择器排除宿主经典窗口类 `mac-os9` / `windows-xp`。

组合类型：`Card`、`ListRow`、`SectionHeader`、`StatusPill`、`StageIndicator`、`SubStepRow`、`StatusGlyph`、`SegmentedControl`、`EmptyState`。状态取 `running`、`done` / `completed`、`paused`、`failed`、`waiting` / `cancelled`。它们只接收显示内容，不了解媒体任务或应用皮肤类型。

`Themes/Defaults.axaml` 提供浅色与深色默认值。宿主覆盖 `Ui*` 颜色及 `Ac*` 尺寸和表面令牌，组合组件使用动态资源，已有实例也会响应皮肤切换。具体契约及 macOS 参考尺寸见仓库 UISPEC.MD 的 7.9 节。经典皮肤的标准控件继续由宿主主题提供；组合控件的圆角和阴影由同名 `Ac*` 令牌覆盖。

## demo 对照与预览

规格源为评审目录的 `components.html` / `components-dark.html`，12 类包括按钮、下拉、选择与开关、数值、文本、滑块、进度、分段、状态、表面与列表、浮层与通知、空状态与滚动条。新增 `LoadingSpinner`、`ProgressRing`、`RangeSlider`、`SplitButton`、`CountBadge`、`Notice`；菜单具名主题为 `AcMenuItem` / `AcContextMenu`。按钮还支持 `destructive`（浅红无描边）、`icon`、`ghost`、`small`、`large`、`shell`。字段支持 `error` 和原生验证错误，TextBox 的 `AcceptsReturn` 自动启用多行规格。

`ac-hover`、`ac-pressed`、`ac-focus` 为预览确定状态，与真实伪类使用相同资源。阶段卡片默认有 0.5px 细边；分类专用布局用 `ShowIdleBorder=false`。范围滑块两个端点可拖动，也可用方向键、Home/End 调整。

仓库中的交互预览（可切换浅色/深色）：

```sh
DISPLAY=:11 AVAMEDIA_CHROME=mac XDG_DATA_HOME=/tmp/avademo/data   ~/.dotnet/dotnet run --project tools/AvaMedia.AiProgressCapture -- --gallery-view
```

使用 `--gallery` 将全部 12 类实控件分别截图；`compare_components.py` 将 demo 缩到相同 1x 尺寸后并排比较，不裁切组件。预览布局清单仅保存 HTML 的位置和示例内容，不含 CSS 绘制结果或 PNG，截图全部由 Avalonia 组件渲染。评审示例不进入应用任务界面。

列表 `.ac.media` 使用 56px 行，普通列表为至少 36px；`.ac.surface` 可包装已有 ListRow，保留虚拟化。标签式 SegmentedControl 使用 `AcTabSegmentItem`。数值框最小宽通过 `AcNumericMinWidth`（91）和 `.compact` 的 `AcNumericCompactMinWidth`（64）动态调整。

浮层 `MaterialSurface`：macOS 原生 PopupRoot 请求 `AcrylicBlur, None`，实际获得模糊后才显示每实例的 ExperimentalAcrylicMaterial。下拉、ToolTip 和 Popover 模板已接入；内嵌/覆盖浮层、不支持的平台和经典皮肤继续用 AcMenuFill 半透明画刷。AcMaterialEnabled 可关闭材质；AcMaterialTintColor/AcMaterialTintOpacity/AcMaterialOpacity 控制材质。
