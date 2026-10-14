# AvaMedia 界面设计规范

本文件是 AvaMedia 桌面端（Avalonia .NET 8）、编辑器和远程网页管理（AvaMedia.Browser）的统一视觉与交互规范，设计代号“AvaMedia macOS 26 light”。参考图在 [docs/ui/](docs/ui/)。

- 任何界面改动前先读本文件和参考图；新界面必须能在这里找到依据，找不到时先补规范再写代码。
- 第 1 节是硬性规则；尺寸、专项覆盖及例外按 1.3 的优先级解释。参考图只说明结构，不替代当前令牌。
- 各节“参考代码”均摘自仓库现有文件，路径相对仓库根目录，供复制使用；代码有改动时以源文件为准。

## 目录

1. 设计约束（MUST / MUST NOT）
2. 设计原则
3. 布局
4. 颜色与主题（令牌、强调色、皮肤）
5. 字体与文字
6. 间距、圆角、行高、阴影
7. 控件
   7.1 按钮 · 7.2 分段控件与筛选胶囊 · 7.3 列表 · 7.4 任务行与任务列表 · 7.5 侧栏工具树 · 7.6 对话框 · 7.7 进度与状态图标 · 7.8 复选框、开关与图标 · 7.9 通用组件与宿主契约
8. 窗口与标题栏（按平台）
9. 状态（交互态、任务态、空状态）
10. 专用界面：10.1 编辑器 · 10.2 媒体整理 · 10.3 预览投影 · 10.4 预览比例 · 10.5 人物详情 · 10.6 播放器单帧 · 10.7 多宫格 · 10.8 文件选择器 · 10.9 菜单栏 · 10.10 设置 · 10.11 独立媒体窗口 · 10.12 阶段与纸页边界
11. 远程网页与手机
12. 文案与本地化
13. 规范内矛盾的处理记录
14. 界面审计与截图验收

---

## 1. 设计约束（MUST / MUST NOT）

### 1.1 必须（MUST）

1. 整体遵循 macOS 26 / Apple 风格：浅色为主要设计目标，系统蓝、发丝线、胶囊按钮、白色卡片。
2. 主窗口始终保持“侧栏工具树 + 持久任务队列”；切换分类和打开工具不得替换、隐藏队列或将主窗口变成工具工作区。工具保留无 Owner 独立窗口，初次打开优先放主窗口旁，后续记住用户位置；屏幕不足时允许顶层窗口重叠，主窗口队列仍可操作和独立切换。详见 1.3、3、8。
3. 工具树同一时间只展开一个分类。
4. 颜色一律引用主题资源（`DynamicResource`，如 `UiAccent`、`ShellBlue`、`QcAccent`），不硬编码颜色；新颜色先作为资源键加入各皮肤的主题字典。
5. 简体中文（zh-CN）文案必须完整显示、不被裁切：控件宽度按中文内容留足，必要时允许换行；只有文件名等用户数据可省略，并提供完整悬停提示。
6. 文档、发布和评审用截图使用 zh-CN 界面。
7. 只有图标的按钮必须有 ToolTip 和无障碍名称（`AutomationProperties.Name`）。
8. 快捷键全部保留且照常可用，只写在悬停提示（tooltip）中。
9. 其他皮肤（深色、Mac OS 9 Platinum、Windows XP Luna）必须保持可用，不能出现不可读文字或错位。
10. 窗口外观统一通过 `MainWindow.WindowChrome` / `Controls/WindowChrome.cs` 复用，不另写标题栏。
   现代皮肤主窗口顶部整行留白（包括侧栏红绿灯周围、标题和工具栏之间）可按住左键拖动窗口；“任务列表”行内留白同样可拖动。双击沿用缩放窗口行为，按钮、输入控件和侧栏分隔条保留自身操作。
11. 只显示真实数据；没有数据时显示简短空状态文字。
12. 单帧图片统一从播放器“截取当前帧”导出；主窗口、首页、新建任务、菜单、原生菜单、媒体路由及远程网页 / MCP 不提供独立的“导出帧”工具或任务类型。播放器按钮、菜单与 Ctrl+E / Cmd+E 保留，多宫格截图继续使用共享抽帧与图标资源。参考图中的旧工具入口不再显示；视频分类按当前目录和 Beta 设置动态计数。

### 1.2 禁止（MUST NOT）

1. 界面任何位置不使用 emoji（图标、装饰、文案、状态均不可）。
2. 不提供搜索框（工具树和工具列表不加搜索）。
3. 不使用虚线拖放区和“拖到这里”提示框。
4. 不在界面上直接显示快捷键：按钮上的键帽（如 [S] / [I] / [O] / Ctrl D）、底部快捷键提示行等都不要。
5. 任务区不放工具入口；工具只在工具树 / 侧栏中出现。
6. 一个区域（一屏）中不放多个蓝色主按钮。
7. 状态色不填满整行。
8. 不显示占位或假数据。
9. 不混用其他图标库；不叠加多层装饰阴影；媒体预览的柔和投影按第 10.3 节执行。

### 1.3 优先级、持久队列与明确例外

- 硬性规则及本节明文例外优先；视觉数值按“组件 `Themes/Defaults.axaml` 默认 → 平台令牌（5.1）→ 皮肤 → 窗口专项令牌”覆盖。组件默认以 AvaMedia.Controls 当前实现为准；业务只选角色和动态资源，不以局部常量纠正文档或覆盖平台度量。正文和 demo 中的数字是默认参考值。
- 持久队列采用独立窗口方案：主窗口只有侧栏与队列；普通工具、任务详情、设置及帮助使用无 Owner 顶层窗口，保留任务栏 / Dock 入口。无需把业务内容嵌入主窗口，也不新增右侧详情列。工具初次定位优先主窗口所在屏幕的右侧、其次左侧；空间不足时在该屏幕工作区居中并裁限，允许重叠，不贴屏幕左上角或自动跳到另一屏；记忆位置失去有效屏幕时重新定位。位置记忆不改变系统 Owner、置顶或 DWM 策略。
- 模态确认及确实需要确认后返回编辑结果的表单、系统原生文件选择器允许临时遮挡 / 阻塞调用窗口；播放器 / 看图全屏允许覆盖队列。退出后队列保持原布局与任务状态。应用内文件选择器仍为独立非模态窗口（10.8）。
- 搜索和虚线拖放区禁令约束应用自建界面；系统原生对话框保留平台行为。流程连线、图表辅助线与经典焦点框可以为虚线，不扩展为拖放提示。
- 导航动作与状态符号用 Lucide；应用标识、项目 v2 / Mac OS 9 功能位图、模型标识、真实媒体封面属于既有资源白名单，不属于 emoji，也不新增另一套图标库。
- 同一区域的同一任务状态只用一个 `StatusPill`；相邻文本仅补活动、百分比、体积或真实错误原因。主队列数量只在任务栏一处；错误原因允许与失败胶囊并存。消费者在各专项任务接入。
- 主窗口侧栏 / 队列 / 状态栏、设置 v2、编辑器和多宫格保留专项度量（6、10）；普通 ListBox 的蓝底白字活动选择不覆盖任务表的浅蓝 + 3px 条，也不覆盖文件选择器自己的行容器。

参考代码（快捷键只进 tooltip）——`src/AvaMedia.Desktop/EditorWindow.QuickLayout.cs`：

```csharp
ToolTip.SetTip(UndoSegmentButton, Localization.Text("撤销") + " (Ctrl Z)"); ToolTip.SetTip(RedoSegmentButton, Localization.Text("重做") + " (Ctrl Y)");
```

`src/AvaMedia.Desktop/EditorWindow.axaml`（图标按钮：tooltip + 无障碍名称）：

```xml
<Button Classes="ib small" Click="DuplicateSegmentClick" IsEnabled="{Binding #DuplicateSegmentButton.IsEnabled}" ToolTip.Tip="复制片段 (Ctrl D)" AutomationProperties.Name="复制片段"><local:LucideIcon Kind="copy" Width="14" Height="14"/></Button>
```

`src/AvaMedia.Desktop/Styles/ControlRoles.axaml`（所有 TextBlock 被裁切时自动给出完整提示）：

```xml
<Style Selector="TextBlock"><Setter Property="controls:OverflowTip.IsEnabled" Value="True"/></Style>
```

---

## 2. 设计原则

- **任务优先**：左侧选工具、右侧看队列，两者同屏；工具入口与任务区职责分离。
- **克制**：一个区域最多一个主按钮；状态色只点缀图标、文字和进度条。
- **真实**：所有计数、地址、状态来自真实数据（远程网页的计数来自服务器，不在本地估算）。
- **平台一致**：使用平台窗口外观（见第 8 节），字体跟随系统。
- **角色而非外观**：业务界面只选择控件角色（`primary`、`dialog-action`、`caption` 等 Classes），外观与尺寸由皮肤资源提供。
- **功能不因换皮丢失**：所有已有功能和快捷键在各皮肤、各模式下都必须可达。

---

## 3. 布局

- 主窗口为“侧栏 + 内容”两栏：侧栏为浮动圆角卡片（底色 `ShellSidebar` `#F1F4FA`），内容区白色卡片承载任务列表。
- 页面外边距：桌面 18、手机 16；通用 Card 内边距 `AcCardPadding=16`，卡片间距 `AcGap=12`；主窗口、设置与编辑器使用专项令牌。
- 设置窗口按分组卡片排列；“远程管理与 MCP”只显示真实数据（地址、状态、最近客户端）。
- 桌面任务详情是无 Owner 独立窗口，与普通工具共用标题框架和位置规则（8）；主窗口不嵌入详情面板。远程网页桌面右侧检查器 / 手机整页仍按第 11 节。

参考代码 `src/AvaMedia.Desktop/Styles/MainShell.axaml`：

```xml
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) Border.feature-sidebar"><Setter Property="Margin" Value="8"/><Setter Property="CornerRadius" Value="18"/><Setter Property="Background" Value="{DynamicResource ShellSidebar}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="BoxShadow" Value="0 0 0 0.5 #1A000000, 0 4 16 -6 #26000000"/><Setter Property="Padding" Value="2,6,2,4"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) Border.queue-card"><Setter Property="Margin" Value="14,0,14,0"/><Setter Property="CornerRadius" Value="12"/><Setter Property="Background" Value="{DynamicResource UiSurface}"/><Setter Property="BoxShadow" Value="0 0 0 0.5 #1F000000"/><Setter Property="ClipToBounds" Value="True"/></Style>
```

现代皮肤 / 经典皮肤专属元素用类名切换（同一文件）：

```xml
<Style Selector=":is(Control).modern-only"><Setter Property="IsVisible" Value="False"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) :is(Control).modern-only"><Setter Property="IsVisible" Value="True"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) :is(Control).classic-only"><Setter Property="IsVisible" Value="False"/></Style>
```

---

## 4. 颜色与主题

### 4.1 设计令牌（浅色）

| 令牌 | 值 | 用途 | 资源键（示例） |
| --- | --- | --- | --- |
| 页面背景 | `#F2F3F7` | 窗口与网页底色 | `Palette.Page`（网页） |
| 侧栏底色 | `#F1F4FA` | 桌面左侧导航、工具树 | `ShellSidebar` |
| 卡片 / 表面 | `#FFFFFF` | 卡片、列表、检查器 | `UiSurface`、`ShellWindow` |
| 填充 | `#F0F0F3` | 次级按钮、分段控件底、片段列表底 | `Fill`（网页） |
| 分隔线 | `#E5E5EA` 1 px | 列表行之间的发丝线 | `ShellHeaderLine`、`Hairline`、`QcLine` |
| 主文字 | `#1D1D1F` | 标题、正文 | `ShellLabel`、`TextPrimary`、`QcText` |
| 次文字 | `#86868B` | 说明、时间、元数据 | `ShellSecondary`、`TextSecondary` |
| 系统蓝 | `#007AFF` | 主按钮、选中、链接、进度 | `ShellBlue`、`UiTreeSelection`、`QcAccent`、`Blue` |
| 浅蓝 | `#EAF3FF` | 选中行、工具徽标底 | `Palette.BlueSoft` |
| 成功 | `#34C759`（文字 `#248A3D`） | 已完成、开关开启、在线 | `ShellGreen` |
| 暂停 / 警告 | `#FF9500` | 已暂停、提醒横幅图标 | `Palette.Orange` |
| 失败 / 危险 | `#FF3B30` | 失败、删除 | `ShellRed` |
| 等待 | `#86868B` | 等待、已取消 | `ShellWait` |
| 横幅 | 底 `#FFF8E6`，文字 `#8A5A00` | 恢复提示 | `Palette.Banner` / `BannerText` |

深色皮肤对应值由各资源字典的 `Dark` 分支提供（如 `ShellBlue` `#0A84FF`、`ShellRed` `#FF453A`），代码不得自行换算。

参考代码 `src/AvaMedia.Desktop/Styles/MainShell.axaml`（主题字典写法，节选）：

```xml
<ResourceDictionary.ThemeDictionaries>
  <ResourceDictionary x:Key="Light">
    <SolidColorBrush x:Key="ShellSidebar" Color="#F1F4FA"/>
    <SolidColorBrush x:Key="ShellHeaderLine" Color="#E5E5EA"/>
    <SolidColorBrush x:Key="ShellSelected" Color="#14007AFF"/>
    <SolidColorBrush x:Key="ShellLabel" Color="#1D1D1F"/>
    <SolidColorBrush x:Key="ShellSecondary" Color="#86868B"/>
    <SolidColorBrush x:Key="ShellBlue" Color="#007AFF"/>
    <SolidColorBrush x:Key="ShellTrack" Color="#E5E5EA"/>
    <SolidColorBrush x:Key="ShellRed" Color="#FF3B30"/>
    <SolidColorBrush x:Key="ShellGreen" Color="#34C759"/>
    <SolidColorBrush x:Key="ShellGreenText" Color="#248A3D"/>
    <SolidColorBrush x:Key="ShellWait" Color="#86868B"/>
  </ResourceDictionary>
  <ResourceDictionary x:Key="Dark">
    <SolidColorBrush x:Key="ShellSidebar" Color="#2A2C31"/>
    <SolidColorBrush x:Key="ShellBlue" Color="#0A84FF"/>
    <SolidColorBrush x:Key="ShellRed" Color="#FF453A"/>
    <SolidColorBrush x:Key="ShellGreen" Color="#30D158"/>
  </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

网页调色板 `src/AvaMedia.Browser/Ui/Palette.cs`（节选）：

```csharp
/// <summary>macOS 26 light palette shared by the remote console.</summary>
internal static class Palette
{
    public static readonly Color BlueColor = Color.Parse("#007AFF");
    public static readonly IBrush Page = new SolidColorBrush(Color.Parse("#F2F3F7"));
    public static readonly IBrush Hairline = new SolidColorBrush(Color.Parse("#E5E5EA"));
    public static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#F0F0F3"));
    public static readonly IBrush BlueSoft = new SolidColorBrush(Color.Parse("#EAF3FF"));
    public static readonly IBrush Banner = new SolidColorBrush(Color.Parse("#FFF8E6"));
    public static readonly IBrush BannerText = new SolidColorBrush(Color.Parse("#8A5A00"));
}
```

C# 中引用主题资源（而不是写死颜色）`src/AvaMedia.Desktop/FolderClassificationWindow.Board.cs`：

```csharp
view.Button.Bind(BorderBrushProperty, new DynamicResourceExtension(selected ? "UiAccent" : "UiBorder"));
```

自绘控件默认绑定强调色 `src/AvaMedia.Desktop/Controls/AudioWaveform.cs`：

```csharp
public AudioWaveform() { MinHeight = 100; Bind(AccentProperty, new DynamicResourceExtension("UiAccent")); }
```

- 分类界面颜色通过 `ClassifyUi.Resource` 动态绑定到所属控件的资源；`UiFill`、`UiClassificationPage`、`UiAccentSoft`、`UiAccentDescription`、`UiSuccessFill`、`UiBanner`、`UiBannerText`、`UiTrack`、`UiClassificationNote` 在浅色、深色、Platinum、XP 中分别定义。媒体采样得到的真实颜色仍直接显示。
- 分类主按钮使用 `UiClassificationPrimary` / `UiClassificationPrimaryText`，规则卡片选中态使用 `UiClassificationRuleFace` / `UiClassificationRuleText`；经典皮肤采用原生浅色按钮底与深色文字。`UiClassificationPillBorder` 保留原生按钮边框；`UiCardShadow` / `UiSegmentShadow` / `UiFocusShadow` / `UiThumbnailShadow` 在经典皮肤中为空。现代提交模板排除 Mac OS 9 / XP。
- 网页 `RemoteTheme.axaml` 中样式、模板和阴影均引用主题资源；成功文字使用 `#248A3D`、横幅使用 `#FFF8E6`、等待文字使用 `#86868B`。

### 4.2 强调色

- 强调色只通过资源键提供：通用 `UiAccent` / `UiAccentBorder` / `UiAccentText` / `UiPrimaryHover` / `UiPrimaryPressed` / `UiSelection`；主窗口 `ShellBlue` / `ShellSelected`；工具树 `UiTreeSelection` / `UiTreeIcon`；快速剪辑 `QcAccent`；Avalonia 内置控件 `ThemeAccentColor` / `ThemeAccentBrush*`。
- 现代皮肤已有蓝、紫、粉、红、橙、黄、绿、灰八种强调色预设；主题可跟随系统。`Skin.Apply` / `Skin.Accent.cs` 覆盖 Ui / Shell / Qc / Avalonia 及 `AcSelection`、`AcFocusShadow` 资源；控件不判断预设名。经典皮肤保留原生强调色。
- 现代皮肤默认蓝色预设为系统蓝 `#007AFF`（深色 `#0A84FF`）。`Styles/UiStyles.axaml` 的通用键与内置控件强调色、`UiNavigationSelection` 已迁移到对应系统蓝（见第 13 节）。

参考代码 `src/AvaMedia.Desktop/Styles/UiStyles.axaml`（节选）：

```xml
<SolidColorBrush x:Key="UiAccent" Color="#007AFF" />
<SolidColorBrush x:Key="UiAccentBorder" Color="#007AFF" />
<SolidColorBrush x:Key="UiAccentText" Color="#FFFFFF" />
<SolidColorBrush x:Key="UiPrimaryHover" Color="#0A6CE0" />
<SolidColorBrush x:Key="UiPrimaryPressed" Color="#005FCC" />
<Color x:Key="ThemeAccentColor">#007AFF</Color>
```

### 4.3 皮肤

| 皮肤 | 菜单名 | ThemeVariant | 强调色 | 窗口框架 |
| --- | --- | --- | --- | --- |
| 浅色（主要目标） | 经典浅色 | `Light` | `#007AFF` | 平台窗口外观（第 8 节） |
| 深色 | 深色 | `Dark` | `#0A84FF` | 平台窗口外观 |
| Mac OS 9 | Mac OS 9 · Platinum | `Skin.MacOS9` | `#3D4E80` | `PlatinumWindowFrame` |
| Windows XP | Windows XP · Luna | `Skin.WindowsXP` | `#215DC6`（树选中 `#316AC5`） | `WindowsXPWindowFrame` |

- Mac OS 9 / XP 为自定义框架皮肤，样式表按需加载；窗口类名分别为 `mac-os9` / `windows-xp`，现代样式选择器一律排除它们（`:not(.mac-os9):not(.windows-xp)`）。
- Platinum 保留原生浮雕按钮及清晰文字（关闭抗锯齿）；无圆角、无 Lucide 分类图标、灰色渐变分类条。
- 深色使用对应 `Dark` 分支令牌；编辑器深色使用 Qc 令牌。
- 命令行 `--macos9` / `--winxp` 可直接以对应皮肤启动，用于截图和验证。

参考代码 `src/AvaMedia.Desktop/Skin.cs`：

```csharp
public static ThemeVariant MacOS9 { get; } = new("MacOS9", ThemeVariant.Light);
public static ThemeVariant WindowsXP { get; } = new("WindowsXP", ThemeVariant.Light);
public static bool UsesCustomChrome(ThemeVariant variant) => variant == MacOS9 || variant == WindowsXP;
public static void Apply(string name, string? accent = null)
{
    Dispatcher.UIThread.VerifyAccess();
    if (name == "MacOS9") EnsureSkinStyles("Platinum");
    if (name == "WindowsXP") EnsureSkinStyles("WindowsXP");
    Application.Current!.RequestedThemeVariant = name switch { "System" => ThemeVariant.Default, "MacOS9" => MacOS9, "WindowsXP" => WindowsXP, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Light };
    ApplyAccent(accent);
}
```

`src/AvaMedia.Desktop/MainWindow.axaml`（皮肤菜单）：

```xml
<MenuItem Header="皮肤"><MenuItem Header="经典浅色" Click="LightClick"/><MenuItem Header="深色" Click="DarkClick"/><MenuItem Header="Mac OS 9 · Platinum" Click="MacOS9Click"/><MenuItem Header="Windows XP · Luna" Click="WindowsXPClick"/></MenuItem>
```

`src/AvaMedia.Desktop/Styles/CategoryNavigation.axaml`（按皮肤提供同名键，节选）：

```xml
<ResourceDictionary x:Key="{x:Static local:Skin.MacOS9}">
  <SolidColorBrush x:Key="UiTreeSelection" Color="#3D4E80"/>
  <CornerRadius x:Key="UiNavigationRailRadius">0</CornerRadius>
  <CornerRadius x:Key="UiNavigationPanelRadius">1</CornerRadius>
  <CornerRadius x:Key="UiNavigationButtonRadius">2</CornerRadius>
</ResourceDictionary>
<ResourceDictionary x:Key="{x:Static local:Skin.WindowsXP}">
  <SolidColorBrush x:Key="UiNavigationRail" Color="#215DC6"/>
  <SolidColorBrush x:Key="UiTreeSelection" Color="#316AC5"/>
</ResourceDictionary>
```

`src/AvaMedia.Desktop/App.MediaAi.cs`：

```csharp
if (args.Contains("--winxp")) settings.Theme = "WindowsXP";
else if (args.Contains("--macos9")) settings.Theme = "MacOS9";
```

---

## 5. 字体与文字

### 5.1 平台字体、渲染与尺寸令牌

桌面保持一套布局和控件模板。启动按真实操作系统加载 `Styles/Platform/Platform.macOS.axaml`、`Platform.Windows.axaml` 或 `Platform.Linux.axaml`；平台层位于 UiStyles 之后、ComponentSkins 和经典皮肤之前，经典皮肤可覆盖同名资源。`AVAMEDIA_CHROME=mac` 不影响字体与平台令牌。

| 令牌 / 规则 | macOS | Windows | Linux |
| --- | --- | --- | --- |
| `UiFont` / 默认字体 | 系统 SF（`.AppleSystemUIFont` → `.SF NS` → SF Pro Text）；中文 PingFang SC | Segoe UI Variable Text → Segoe UI；中文 Microsoft YaHei UI | 内置 Inter；中文内置 Noto Sans SC |
| 最末字体回退 | 内置 Noto Sans SC | 内置 Inter → Noto Sans SC | 内置 Noto Sans SC；子集外按系统字体回退 |
| `UiNumericFont` | SF Mono → Menlo | Cascadia Mono → Consolas | DejaVu Sans Mono → Noto Sans Mono |
| `UiFontBody` / `UiFontCaption` / `UiFontTitle` | 13 / 12 / 17 | 13 / 12 / 17 | 13 / 12 / 17 |
| `AcBodyFont` / `AcCaptionFont` / `AcSmallFont` | 13 / 12 / 12 | 13 / 12 / 12 | 13 / 12 / 12 |
| `AcButtonHeight` / `AcShellButtonHeight` / `AcFieldHeight`（含 UiFieldHeight） | 28 / 30 / 24 | 30 / 30 / 28 | 32 / 32 / 28 |
| `UiPropertyRowHeight` | 32 | 34 | 34 |
| `AcSmallHeight` / `AcLargeHeight` | 24 / 36 | 24 / 36 | 26 / 38 |
| `AcListHeight` / `AcTextRowHeight` / `UiSettingRowHeight` | 56 / 36 / 40 | 58 / 38 / 42 | 58 / 38 / 42 |
| `AcButtonRadius` / `AcShellButtonRadius` | 14 / 15 | 15 / 15 | 16 / 16 |
| `AcFieldRadius` / `UiInputRadius` / `UiControlRadius` | 6 / 7 / 4 | 4 / 4 / 4 | 6 / 6 / 6 |
| `AcCardRadius` / `AcListRadius` / `AcGroupRadius` | 12 / 8 / 10 | 8 / 4 / 6 | 10 / 6 / 8 |
| `UiCardRadius` / `UiSidebarRadius` / `UiSettingsGroupRadius` | 14 / 18 / 11 | 8 / 8 / 6 | 12 / 12 / 8 |
| 默认文字渲染 | Antialias（灰度） | SubpixelAntialias | SubpixelAntialias |

- FileUi 独立宿主保留 Fb 令牌；在桌面宿主中，现代 FbActionHeight / FbNavigationHeight / FbTextRowHeight 与 AcShellButtonHeight / AcButtonHeight / AcTextRowHeight 对齐，FbCardRadius / FbNavigationRadius 与 UiCardRadius / UiInputRadius 对齐，经典皮肤保留本地值。
- 不复制平台专属页面；正文、说明、常规按钮、输入框、列表与内部卡片引用动态令牌。窗口尺寸、媒体视口尺寸、图标大小和按内容计算的几何不套用控件高度令牌。窗口框架仍遵循第 8 节，不由本平台层设置标题栏高度或系统装饰。
- FontManagerOptions 与 UiFont 使用同一策略；macOS 默认字体和 UiFont 均显式优先系统 SF 字体族，依次回退苹方和内置 Noto；不只使用 `$Default`，因为 Skia 的默认面可能是 Helvetica。Windows 保留 Segoe UI 和雅黑，缺少系统字体时由内置字体承接。Linux 不依赖安装 CJK 字体；子集外字符继续使用系统字体匹配。
- 内置 Inter（Regular / Medium / SemiBold / Bold，文本光学尺寸 14）及 Noto Sans SC（Regular / Medium / Bold）为静态 TTF 子集，避免依赖可变字体字重实例支持。两者均为 SIL OFL 1.1；版权及全文分别在 `licenses/fonts/inter/OFL.txt` 和 `licenses/fonts/noto-sans-sc/OFL.txt`。子集覆盖 GB2312、拉丁/拼音、常用标点、全角字符和仓库界面用汉字；确切覆盖数量、体积、哈希见 `Assets/Fonts/subsets.json`，更新脚本为 `scripts/Subset-DesktopFonts.py`。
- 字号使用整数 DIP，说明 12、正文 13、强调 14、卡片标题 15、页面标题 17；不使用 11.5 / 12.5 等小数字号。布局保持 UseLayoutRounding；细描边、圆形几何与实际媒体坐标可用小数，不强制取整。计数和时间码保留 `tnum`。
- Windows/Linux 申请 SubpixelAntialias；透明或离屏渲染可能由 Skia 降为灰度，不能以 RenderTargetBitmap 截图证明 LCD 子像素效果。macOS 使用灰度抗锯齿；Platinum 自有像素渲染规则优先。不要用 Skia GPU 缓存参数冒充文字 hinting 设置。

### 5.2 Linux 缩放

- 外观设置提供“跟随系统、100%、125%、150%、200%”，仅 Linux 显示，保存后重启生效；窗口、菜单及弹出层共用同一后端缩放，不只放大文字，不改变标题栏策略。
- 明确的用户百分比优先于环境变量，按绝对比例应用；内部设置 `AVALONIA_SCREEN_SCALE_FACTORS=1` 与所选 `AVALONIA_GLOBAL_SCALE_FACTOR`，防止与 Xft.dpi 再相乘。
- “跟随系统”保留 `AVALONIA_GLOBAL_SCALE_FACTOR` / `AVALONIA_SCREEN_SCALE_FACTORS` 的显式配置；其次保留 Avalonia 原生 Qt 配置与 Xft.dpi。仅上述来源均未提供时，读取有效 GDK_SCALE（并乘 GDK_DPI_SCALE）作为绝对比例。无有效来源则由 Avalonia 按 1.0 处理。GDK 和 Qt / Xft 不叠加。
- Avalonia 11.3.22 在 Linux 使用 X11 / XWayland；Wayland 合成器分数缩放、多屏不同 DPI 和实机 LCD 渲染需要设备验收。环境变量在 AppBuilder 初始化平台之前设置；不修改系统 Xresources。


### 5.3 字体角色参考

- 桌面字体按 5.1 的平台策略；网页使用内置 Noto Sans SC 子集，像素字体只作回退。
- 字号：大标题 26 粗（手机网页大标题 28）；页面标题 17 粗；卡片标题 15 粗；正文 13；说明 12；等宽时间码 12。
- 时间码：编辑器为 `mm:ss.mmm / 总时长`；快速剪辑字段为 `mm:ss.fff`，超过一小时为 `h:mm:ss.fff`；数字等宽。

参考代码 `src/AvaMedia.Desktop/Styles/UiStyles.axaml`：

```xml
<FontFamily x:Key="UiFont">avares://AvaMedia.Desktop/Assets/Fonts/Inter#Inter, avares://AvaMedia.Desktop/Assets/Fonts/NotoSansSC#Noto Sans SC</FontFamily>
```

`src/AvaMedia.Browser/Ui/RemoteTheme.axaml`（网页文字角色）：

```xml
<Style Selector="TextBlock.secondary"><Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/><Setter Property="FontSize" Value="12"/></Style>
<Style Selector="TextBlock.caption"><Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/><Setter Property="FontSize" Value="11.5"/></Style>
<Style Selector="TextBlock.title"><Setter Property="FontSize" Value="17"/><Setter Property="FontWeight" Value="Bold"/></Style>
<Style Selector="TextBlock.largeTitle"><Setter Property="FontSize" Value="28"/><Setter Property="FontWeight" Value="Bold"/></Style>
<Style Selector="TextBlock.mono"><Setter Property="FontFamily" Value="avares://AvaMedia.Browser/Fonts#Noto Sans CJK SC"/><Setter Property="FontSize" Value="11.5"/></Style>
```

`src/AvaMedia.Desktop/Styles/MainShell.axaml`（主窗口标题）：

```xml
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) TextBlock.modern-title-text"><Setter Property="FontSize" Value="15"/><Setter Property="FontWeight" Value="SemiBold"/><Setter Property="Foreground" Value="{DynamicResource ShellLabel}"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) TextBlock.modern-subtitle"><Setter Property="FontSize" Value="11"/><Setter Property="Foreground" Value="{DynamicResource ShellSecondary}"/></Style>
```

---

## 6. 间距、圆角、行高、阴影

- 间距：基础单位 4；通用 Card 内边距 `AcCardPadding=16`；卡片间距 `AcGap=12`；页面外边距桌面 18、手机 16。
- 圆角：通用 Card `AcCardRadius=12`；按钮 `AcButtonRadius=14`；字段 `AcFieldRadius=6`；缩略图 `AcThumbRadius=6`；分段 `AcSegmentRadius=8` / `AcSegmentItemRadius=6`。平台覆盖见 5.1；`UiCardRadius` 是宿主专项表面值，不是 Card 默认。主窗口侧栏卡片 18、队列卡片 12、状态栏 10 为主窗口专用值。
- 行高：任务行 ≥ 56；文件行 29；设置行 42–44；片段 / 区域行 32；工具树分类行 28、工具行 26、小节标题 20（Platinum 为 24 / 21 / 18）。
- 阴影：卡片 `0 1 3 #14000000`；悬浮按钮 `0 6 16 #55007AFF`；不叠加多层装饰阴影（主窗口卡片允许“0.5 px 描边 + 一层柔影”的组合，用于替代边框）。
- 尺寸通过资源键提供（如 `UiFieldHeight`、`UiDialogActionHeight`、`UiTransportSize`、`UiTableHeaderHeight`、`UiQueuePreviewWidth`），业务代码不写死。

参考代码 `src/AvaMedia.Desktop/Styles/ControlRoles.axaml`：

```xml
<Style Selector="Button.dialog-action">
  <Setter Property="MinWidth" Value="{DynamicResource UiDialogActionWidth}"/>
  <Setter Property="Height" Value="{DynamicResource UiDialogActionHeight}"/>
  <Setter Property="MinHeight" Value="{DynamicResource UiDialogActionHeight}"/>
  <Setter Property="Padding" Value="{DynamicResource UiDialogActionPadding}"/>
</Style>
<Style Selector="Button.step, Button.row-icon">
  <Setter Property="Width" Value="{DynamicResource UiFieldHeight}"/>
  <Setter Property="Height" Value="{DynamicResource UiFieldHeight}"/>
  <Setter Property="Padding" Value="{DynamicResource UiCompactButtonPadding}"/>
</Style>
```

`src/AvaMedia.Desktop/Styles/MainShell.axaml`（工具树行高）：

```xml
<Style Selector="Button.feature-tree-row"><Setter Property="Height" Value="26"/></Style>
<Style Selector="Button.feature-tree-category"><Setter Property="Height" Value="28"/></Style>
<Style Selector="TextBlock.feature-tree-group"><Setter Property="Height" Value="20"/></Style>
```

---

## 7. 控件

### 7.1 按钮

- **胶囊按钮**：默认浅灰填充、深色文字，可带 14 px Lucide 图标。
- `primary`：系统蓝底白字，每个区域最多一个；放在底部操作栏右侧（如“创建任务”）。
- 桌面 large 使用 `AcLargeHeight=36`（平台覆盖见 5.1）；shell 使用 `AcShellButtonHeight`，dialog-action 使用规范角色令牌。网页 / 手机 `Button.large` 的 40 是专项值。
- `danger`：红字（网页为红字 + 浅红底 `#FFECEB`）；`link`：无底色蓝字。
- **图标按钮**：只有图标时必须有 ToolTip 和无障碍名称。
- 网页按钮的悬停与按下反馈只绘制在外层圆角表面；内部内容区保持透明，不叠加基础主题的矩形背景与边框。
- 网页按钮内的文字跟随按钮角色前景色；主按钮及选中按钮为白字，链接与危险操作使用各自语义色。数字加减、返回和移除等纯图标操作保留本地化提示及无障碍名称。
- 禁用：按钮 / 滑块 / 进度 `AcDisabledOpacity=0.4`；文本字段 `AcFieldDisabledOpacity=0.45`。主按钮禁用使用轨道底与次文字；不将同一个透明度强加到所有控件。
- Mac OS 9 / XP 皮肤以原生默认按钮外观表达主按钮。

参考代码 `src/AvaMedia.Desktop/Styles/ControlRoles.axaml`：

```xml
<!-- Classic skins express the primary action through their native default face. -->
<Style Selector="Window:not(.mac-os9):not(.windows-xp) Button.primary">
  <Setter Property="Background" Value="{DynamicResource UiAccent}"/>
  <Setter Property="Foreground" Value="{DynamicResource UiAccentText}"/>
  <Setter Property="BorderBrush" Value="{DynamicResource UiAccent}"/>
</Style>
```

`src/AvaMedia.Desktop/Styles/MainShell.axaml`（主窗口胶囊按钮）：

```xml
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) Button.cap"><Setter Property="Height" Value="30"/><Setter Property="MinHeight" Value="30"/><Setter Property="MinWidth" Value="32"/><Setter Property="Padding" Value="9,0"/><Setter Property="CornerRadius" Value="15"/><Setter Property="Background" Value="Transparent"/><Setter Property="BorderBrush" Value="Transparent"/><Setter Property="BorderThickness" Value="0"/><Setter Property="FontSize" Value="13"/><Setter Property="Foreground" Value="{DynamicResource ShellCapText}"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) Button.cap.primary"><Setter Property="Background" Value="{DynamicResource ShellBlue}"/><Setter Property="Foreground" Value="{DynamicResource Surface}"/><Setter Property="Padding" Value="13,0"/><Setter Property="FontWeight" Value="Medium"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) Button.cap:disabled"><Setter Property="Opacity" Value="0.45"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) Button.cap.primary:disabled /template/ ContentPresenter#PART_ContentPresenter"><Setter Property="Background" Value="{DynamicResource ShellTrack}"/><Setter Property="Foreground" Value="{DynamicResource ShellSecondary}"/></Style>
```

`src/AvaMedia.Browser/Ui/RemoteTheme.axaml`（网页按钮角色，节选）：

```xml
<Setter Property="CornerRadius" Value="999"/>
<Style Selector="Button.primary"><Setter Property="Background" Value="{DynamicResource Blue}"/><Setter Property="Foreground" Value="{DynamicResource Surface}"/><Setter Property="FontWeight" Value="Bold"/></Style>
<Style Selector="Button.large"><Setter Property="Padding" Value="20,9"/><Setter Property="FontSize" Value="15"/><Setter Property="MinHeight" Value="40"/></Style>
<Style Selector="Button.danger"><Setter Property="Foreground" Value="{DynamicResource Red}"/><Setter Property="Background" Value="{DynamicResource RedSoft}"/></Style>
<Style Selector="Button.link"><Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource Blue}"/><Setter Property="Padding" Value="4,2"/><Setter Property="FontWeight" Value="Bold"/></Style>
<Style Selector="Button.icon"><Setter Property="Padding" Value="0"/><Setter Property="Width" Value="30"/><Setter Property="Height" Value="30"/><Setter Property="MinHeight" Value="0"/></Style>
```

桌面代码创建按钮的辅助方法见 `src/AvaMedia.Desktop/Ui.cs`：`Ui.Button(text, action)`、`Ui.DialogButton(text, action, isDefault)`。

### 7.2 分段控件与筛选胶囊

- **分段控件**：浅灰底、选中段白底加细阴影；用于“所有任务 / 文件与任务下发 / 服务器”、“结果 / 处理日志”、复制 / 移动和编辑器模式切换。
- **筛选胶囊**：文字 + 服务器计数（例如“等待 4”），选中为蓝底白字。计数来自服务器，不在本地估算。

参考代码 `src/AvaMedia.Browser/Ui/RemoteTheme.axaml`：

```xml
<Style Selector="Button.segmentLight"><Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="12,5"/><Setter Property="CornerRadius" Value="7"/></Style>
<Style Selector="Button.segmentLight.active"><Setter Property="Background" Value="{DynamicResource Surface}"/><Setter Property="FontWeight" Value="Bold"/></Style>
<Style Selector="Button.segmentLight.active /template/ Border#face"><Setter Property="BoxShadow" Value="{DynamicResource SegmentShadow}"/></Style>
<Style Selector="Button.chip"><Setter Property="Padding" Value="10,4"/><Setter Property="MinHeight" Value="26"/><Setter Property="FontSize" Value="13"/></Style>
<Style Selector="Button.selected"><Setter Property="Background" Value="{DynamicResource Blue}"/><Setter Property="Foreground" Value="{DynamicResource Surface}"/><Setter Property="FontWeight" Value="Bold"/></Style>
```

快速剪辑分段令牌 `src/AvaMedia.Desktop/Styles/QuickClipStyles.axaml`：`QcSegTrack`（`#E9E9EE`）、`QcSegOn`（`#FFFFFF`）。

### 7.3 列表

- **发丝线列表**：白底，行与行之间 1 px `#E5E5EA`，左侧缩进与内容对齐；不画外框和斑马纹。
- 列表头使用 `UiTableHeaderHeight` 高度。

参考代码 `src/AvaMedia.Browser/Ui/RemoteTheme.axaml`（可点击行）：

```xml
<Style Selector="Button.row"><Setter Property="Background" Value="Transparent"/><Setter Property="CornerRadius" Value="0"/><Setter Property="Padding" Value="0"/>
  <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="HorizontalAlignment" Value="Stretch"/></Style>
<Style Selector="Button.row:pointerover /template/ Border#face"><Setter Property="Background" Value="{DynamicResource RowHover}"/></Style>
```

### 7.4 任务行与任务列表

- 每行包含名称（粗体）、“工具 · 来源”、状态列（状态图标 + 文字 + 进度条 + 当前活动）和右对齐的操作按钮。列宽固定，状态列和操作列逐行对齐。行高 ≥ 56。
- 每种状态只显示该状态可用的操作：运行中“暂停 / 停止”，等待“开始 / 暂停 / 停止”，已暂停“恢复 / 停止”，失败“重试”，全部带“详情”。
- 选中行浅蓝底并在左侧显示 3 px 蓝色强调条。
- 独立任务详情窗口：文件名、类型图标、唯一状态和操作，使用“结果 / 文件与参数 / 处理日志”三个分段。结果保留完整原因及真实体积、编码、画质等指标；文件与参数按键值列表显示来源、实际产出与所选参数；处理日志可滚动、选择、复制并显示读取状态。复用 Card、StatusPill、SegmentedControl、TextBox.ac 和公共窗口框架，颜色、间距与字号引用现有动态令牌。
- 所有任务行及右键菜单均提供“查看任务详情”；已完成、失败、取消和进行中的普通任务双击进入详情，等待或暂停且可编辑的任务保留编辑路径。AI 专项任务界面保留原有入口。查看日志直接切到任务详情的日志区，通知只承载简短结果与进入详情的操作，清除通知不影响详情与日志。
- 视频瘦身在无法安全减小体积时显示“无需瘦身”和真实原因，作为已完成的信息结果；明确显示“保留原视频，未生成新文件”。没有实际产出时不显示输出预览、播放、打开输出或拖拽产出操作；预估体积与实际体积分开标注，没有指标显示“—”，不伪造体积或画质评分。
- 桌面任务行订阅队列状态并合并进度通知；远程网页每 3 秒轮询，底部分页“共 N 项 · 第 n 页”。刷新保持行顺序与选中项，不整表重建。
- 任务区只放任务，不放工具入口。

参考代码 `src/AvaMedia.Desktop/MainWindow.axaml`：

```xml
<ListBox Classes="task-list" Grid.Row="2" x:Name="JobList" Padding="0" Background="{DynamicResource UiSurface}" BorderBrush="{DynamicResource UiBorder}" SelectionMode="Multiple" ScrollViewer.HorizontalScrollBarVisibility="Disabled" SelectionChanged="JobSelectionChanged">
  <ListBox.ItemTemplate><DataTemplate><local:JobRowView/></DataTemplate></ListBox.ItemTemplate>
```

`src/AvaMedia.Desktop/Controls/JobRowView.axaml`（固定列宽）：

```xml
<Grid.ColumnDefinitions><ColumnDefinition Width="{DynamicResource UiQueuePreviewWidth}"/><ColumnDefinition Width="1.15*"/><ColumnDefinition Width="1.45*"/></Grid.ColumnDefinitions>
```

`src/AvaMedia.Desktop/Styles/MainShell.axaml`（状态文字与进度条）：

```xml
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list ProgressBar"><Setter Property="Height" Value="4"/><Setter Property="MinHeight" Value="4"/><Setter Property="MaxHeight" Value="4"/><Setter Property="CornerRadius" Value="2"/><Setter Property="Background" Value="{DynamicResource ShellTrack}"/><Setter Property="Foreground" Value="{DynamicResource ShellBlue}"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list TextBlock.queue-state"><Setter Property="FontSize" Value="12"/><Setter Property="Foreground" Value="{DynamicResource ShellWait}"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list TextBlock.queue-state.running"><Setter Property="Foreground" Value="{DynamicResource ShellBlue}"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list TextBlock.queue-state.completed"><Setter Property="Foreground" Value="{DynamicResource ShellGreenText}"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list TextBlock.queue-state.failed"><Setter Property="Foreground" Value="{DynamicResource ShellRed}"/></Style>
```

远程网页轮询 `src/AvaMedia.Browser/ConsoleView.cs`：

```csharp
private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
```

### 7.5 侧栏工具树

- 按“视频 / 音频 / 图片 / 文档 / 工具集”分类的手风琴，**同时只展开一个分类**；展开一个分类时其他分类自动收起。光盘作为工具集内的用途分组，目录原分类和工具 ID 保持不变；侧栏映射以 `FeatureNavigation` 为准。
- 分类行显示图标、名称和数量；展开后按小节列出工具“胶囊”，选中为蓝底白字（`UiTreeSelection` / `UiTreeSelectionText`），图标用 `UiTreeIcon`。
- 不提供搜索框。
- macOS 下工具树从红绿灯下方开始（见第 8 节）。
- Beta 标记使用 `beta-badge`（浅橙底），Platinum 下为灰底黑框。

参考代码 `src/AvaMedia.Desktop/MainWindow.Categories.cs`（单分类展开）：

```csharp
private void CollapseCategories() { _category = ""; ApplyCategoryState(); }

private void ApplyCategoryState()
{
    _updatingTree = true;
    try
    {
        foreach (var (category, item) in _treeCategories)
        {
            var expanded = category == _category;
            if (!expanded && item.Body.IsKeyboardFocusWithin) item.Header.Focus(NavigationMethod.Directional);
            item.Header.IsExpanded = expanded;
            item.Body.IsVisible = expanded;
        }
    }
    finally { _updatingTree = false; }
}
```

`src/AvaMedia.Desktop/Styles/CategoryNavigation.axaml`（浅色）：

```xml
<SolidColorBrush x:Key="UiTreeSelection" Color="#007AFF"/>
<SolidColorBrush x:Key="UiTreeSelectionText" Color="#FFFFFF"/>
<SolidColorBrush x:Key="UiTreeIcon" Color="#007AFF"/>
```

### 7.6 对话框

- 模态提示沿用公共标题框架；正文用 `Sheet` / `.ac-sheet.window`，`AcSheetPadding=18,20,18,16`（左、上、右、下），内容间距 `AcGap=12`，按钮间距 `AcRowGap=8`。window 角色不叠加弹出阴影或圆角；正文不再套带 padding 的 Card。列表内容可滚动，底部右侧只放一个主按钮。
- 对话框按钮使用 `dialog-action` 角色。
- 统一使用 `Ui` 辅助方法创建，不自建窗口。

参考代码 `src/AvaMedia.Desktop/Ui.cs`（签名）：

```csharp
public static async Task<bool> Confirm(Window owner,string title,string message,string action)
public static Task MessageDialog(Window owner, string title, string message, IReadOnlyList<string> details)
public static Task Message(Window owner,string title,string message)
```

调用示例 `src/AvaMedia.Desktop/EditorWindow.axaml.cs`：

```csharp
await Ui.MessageDialog(this, "还有视频未剪辑", "请先剪辑以下视频，或在视频列表中取消勾选，再进入导出选项。", names);
```

### 7.7 进度与状态图标

- 进度条高 4、圆角 2，轨道 `ShellTrack`，填充系统蓝。
- 状态图标（Lucide）：运行中 `loader-circle` 蓝；等待 `clock` 灰；已暂停 `pause` 橙；已完成 `check` 绿；失败 `circle-alert` 红；已取消 `ban` 灰。图标后跟状态文字和百分比。
- 状态色只用于状态图标、状态文字和进度条。

参考代码 `src/AvaMedia.Desktop/Styles/MainShell.axaml`：

```xml
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list controls|LucideIcon.queue-state-icon"><Setter Property="Foreground" Value="{DynamicResource ShellWait}"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list controls|LucideIcon.queue-state-icon.completed"><Setter Property="Foreground" Value="{DynamicResource ShellGreen}"/></Style>
<Style Selector="Window.main-window:not(.mac-os9):not(.windows-xp) ListBox.task-list controls|LucideIcon.queue-state-icon.failed"><Setter Property="Foreground" Value="{DynamicResource ShellRed}"/></Style>
```

阶段卡片按状态取资源键 `src/AvaMedia.Desktop/AiProgress/SpeechStageCard.cs`：

```csharp
: state == JobState.Failed && current ? "UiDanger" : state == JobState.Paused && current ? "UiWarning" : "UiAccent";
```

### 7.8 复选框、开关与图标

- 复选框：14 px 蓝色圆角方块；开关：绿色 iOS 样式。
- 图标：统一使用 Lucide 线性图标（描边 2），通过 `LucideIcon` 控件（`src/AvaMedia.Desktop/Controls/LucideIcon.cs`）使用，不用 emoji、不混用其他图标库。

参考代码 `src/AvaMedia.Desktop/Controls/WindowChrome.cs`：

```csharp
var button = new Button { Content = new LucideIcon { Kind = glyph, Width = 14, Height = 14 }, Classes = { "caption" }, Focusable = false };
```

---

### 7.9 通用组件（AvaMedia.Controls）

#### 7.9.0 方案评估与边界

参考 [Devolutions/avalonia-extensions](https://github.com/Devolutions/avalonia-extensions) 的 MacOS 主题（MIT）。上游说明：2026.6.16 为最后兼容 Avalonia 11 的稳定版本，2026.6.17-avalonia12 及以后的版本进入 Avalonia 12，首个 Avalonia 12 稳定版为 2026.6.23。本项目保持 Avalonia 11.3.22、Themes.Simple 和既有皮肤。

| 做法 | 收益 | 成本与皮肤接入 |
| --- | --- | --- |
| 从 2026.6.16 移植所需 ControlTheme | 可复用成熟的模板、交互态和模板部件 | 必须保留 MIT 许可与版权声明；需要改写上游颜色、尺寸和关联资源，并逐个隔离经典皮肤的模板；禁止整体替换应用主题 |
| 自行编写具名 ControlTheme 与组合控件 | 资源契约直接符合本项目，可保持依赖小、业务无关，按组件逐步接入 | 需要维护交互态、键盘焦点与无障碍；标准输入控件沿用 Simple 的交互部件，组合控件负责统一布局 |

**结论：自行编写。** `src/AvaMedia.Controls` 不引用应用其他项目，拥有默认令牌、具名 ControlTheme 和可复用组合控件，结构可独立打包。宿主在默认令牌之后加载自己的资源；Mac OS 9 / XP 用同名令牌覆盖组合控件的尺寸、圆角、阴影，标准控件继续使用经典皮肤模板。未复制 Devolutions 源码，因此不附加其 MIT 声明；以后若移植代码，必须随移植文件保留 MIT 许可与版权声明。

业务界面只选择角色和内容；通用控件的颜色、圆角、高度和内边距在组件资源中定义。既有窗口的专用布局继续按本规范对应章节执行。详见 [组件库说明](src/AvaMedia.Controls/README.md)。

#### 7.9.1 通用组件 demo 规格、资源键与类名

内边距统一按 Avalonia Thickness 写法（双值为水平、垂直；四值为左、上、右、下）；默认值以组件源码为准，平台覆盖见 5.1。

结构与角色参考 `/workspace/AvaMedia-sidebar-review/components/components.html`、`components-dark.html` 及 `sections/s01..s12-light/dark.png`。HTML 标注不覆盖组件当前默认与平台令牌；桌面尺寸为 DIP（1x 逻辑像素）。组件默认值见 [Defaults.axaml](src/AvaMedia.Controls/Themes/Defaults.axaml)，模板见 [ControlThemes.axaml](src/AvaMedia.Controls/Themes/ControlThemes.axaml)，组合角色见 [Components.axaml](src/AvaMedia.Controls/Themes/Components.axaml)。组件预览与状态样例见 `tools/AvaMedia.AiProgressCapture/ComponentGallery.cs`。

| demo 类别 | 主题键 / 类名 / 类型 | 尺寸与状态规格 | 主题资源 |
| --- | --- | --- | --- |
| 01 按钮 | `AcButton`；`Button.ac.primary/secondary/borderless/danger/destructive/icon/ghost`；`SplitButton` | 常规高 28、圆角 14、内边距 14,0、字 13 Medium；主窗口 30；small 高 22、10,0、字 12；large 高 36、20,0、字 14；图标 28×28；拆分箭头区 8,0、分隔 0.5 | `UiAccent/UiAccentText/UiPrimaryHover/UiPrimaryPressed`；`UiFill/AcFillHover/AcFillPressed`；危险 `AcDangerSoft/AcDangerHover/AcDangerPressed/UiDanger`；无边框 `AcSelection/AcSelectionPressed`；焦点 `AcFocusShadow` 外环 3；禁用 `AcDisabledOpacity` 0.4 |
| 02 下拉与可编辑组合框 | `AcComboBox`、`AcComboBoxItem`；`ComboBox.ac`，`.pulldown`；`AcMenuItem`、`ContextMenu.ac` | 高 24、圆角 6、内边距 10,0,4,0；双箭头蓝块 16×16、圆角 4；编辑箭头区宽 20；菜单圆角 10、内边距 5；行高 24、圆角 5、勾选列 14、分隔 0.5（缩进 10） | `AcField/AcPopupFace/AcHairline/AcMenuFill/AcPopupShadow`；高亮 `UiAccent/UiAccentText`；禁用 `AcTextTertiary`；聚焦外环，编辑输入保留 |
| 03 复选、单选、开关 | `AcCheckBox/AcRadioButton/AcToggleSwitch`；`.ac`、`.error`、开关 `.small` | 选择框 14×14、圆角 3.5（单选圆形）、标签间距 7；单选中心点 6；开关 38×22、圆角 11、白旋钮 18、按下加宽 4；small 26×15、旋钮 13；支持混合、选中悬停/按下、焦点、错误、禁用 | `AcField/AcChoiceBorder/UiAccent/UiAccentText`；开 `UiSuccessFill`、关 `UiTrack`；旋钮 `AcKnob/AcKnobShadow`；焦点 `AcFocusShadow` |
| 04 数值步进器 | `AcNumericUpDown/AcButtonSpinner`；`NumericUpDown.ac` | 字段高 24、默认宽 72、圆角 6、右对齐数字 13；独立步进区 15×22、圆角 5、两段各 11；间距 4；单位字 12；错误 1px 红边与 3px 外晕、消息字 11 | `AcField/AcHairline/AcPopupFace/AcFillPressed`；`UiNumericFont` 数字；`AcErrorShadow/UiDanger` |
| 05 文本框 | `AcTextBox`；`TextBox.ac`、`.multiline`、`.error` | 单行高 24、圆角 6、内边距 8,0、字 13；多行最小高 76、内边距 8,6、行高 1.45；描边 0.5；错误消息间距 6；禁用不透明度 0.45 | `AcField/AcHairline/AcFieldHoverBorder/AcTextTertiary`；聚焦 `UiAccent/AcFocusShadow`；错误 `UiDanger/AcErrorShadow` |
| 06 滑块 | `AcSlider`；`Slider.ac`、`.ticks`；`RangeSlider` | 横向控件高 22，垂直方向交换轨道与刻度的尺寸；轨道 3、圆角 1.5、白圆旋钮 18；刻度 1.5×5、刻度旋钮宽 14 尖底；范围双旋钮；按下放大 1.08、焦点环、禁用 0.4 | `UiTrack/UiAccent/AcKnob/AcKnobShadow/AcFocusShadow/AcTextTertiary` |
| 07 进度与阶段 | `AcProgressBar`；`ProgressBar.ac.paused/failed`；`ProgressRing/LoadingSpinner/StageIndicator/SubStepRow/StatusGlyph` | 条高 4、圆角 2；不确定滑块 30%、1.2s 往返；旋转器 14/22；进度环 44、线宽 3.5；阶段高 64、圆角 10、内边距 12,10、序号 18、字 11 半粗、箭头区 24；当前边 1.5；计时字 11；子步骤图标 14、间距 9、上下 7、标题 13、说明 11 | `UiTrack/UiAccent/UiSuccessFill/UiWarning/UiDanger/UiAccentSoft`；`AcStageRadius/AcStagePadding/AcFocusThickness/AcStageLine/AcStageLineBrush`；计数等宽；分类进度专用非当前阶段无边框 |
| 08 分段与标签栏 | `SegmentedControl`、`.tabs`；`AcSegmentItem` | 高 28、外圆角 8、内边距 2、段圆角 6、内边距 14,0、字 13 Medium；段间 0.5 分隔（选中相邻隐藏）；标签字 13、间距 22、底间距 9、指示条高 2 | `UiFill/AcSegmentFace/AcSegmentShadow/AcHairline/UiAccent`；深色选中 #636366；悬停、焦点、禁用 |
| 09 状态胶囊与徽标 | `StatusPill/CountBadge`；`.ac-status`、`.ac-badge`、`.dot` | 胶囊高 20、圆角 10、内边距 8,0、字 12 Medium、状态点 6；徽标高/最小宽 18、圆角 9、内边距 5,0、字 11 半粗等宽；通知点 8；叠放外边 1.5 | `AcRunningFill/AcSuccessFill/AcWarningFill/AcDangerFill/AcWaitingFill`，对应色 12–14%；文字 `UiAccent/UiSuccess/AcWarningText/UiDanger/UiTextSecondary` |
| 10 卡片、设置分组、列表 | `Card/ListRow/SectionHeader`；`.ac-card/.ac-group/.ac-setting-row/.ac-list-row/.ac-section` | 卡片圆角 12、内边距 16、内部/卡片间距 12；标题 13 半粗、描述 12、下间距 8；设置分组圆角 10、行最小高 40、内边距 12,0、分隔 0.5 左缩 12；列表高 56/36、圆角 8、内边距 10,0、间距 8、缩略图 40/圆角 6、角标高 16、数值 20 Bold 右对齐 | `UiSurface/AcCardFace/AcCardShadow/AcHairline/UiFill/AcSelection`；活动选择 `UiAccent/UiAccentText`；常态、悬停、非活动选择、活动选择、禁用 |
| 11 浮层、表单与通知 | `AcToolTip/AcMenuItem/AcPopover`；`.ac-tooltip/.ac-popover/.ac-sheet/.ac-notice`；`Notice/Popover` | 提示字 12、内边距 8,4、圆角 6、延迟 0.6s；气泡圆角 12、内边距 14、默认宽 240、箭头 12；确认表单宽 300、圆角 16、内边距左18/上20/右18/下16、图标 44、标题 13 Bold、按钮间距 8；通知宽 340、圆角 10、内边距 12,10、色条宽 3、图标 16 | `AcMenuFill/AcPopupShadow/UiText/UiTextSecondary`；通知 `AcNoticeFace/UiAccent/UiSuccessFill/UiDanger`；警告 `AcBanner/AcBannerText/UiWarning` |
| 12 空/错误状态与滚动条 | `EmptyState`、`.error`；`AcScrollBar`、`ScrollBar.ac` | 空/错状态居中图标 40、线宽 1.4、标题 13 半粗、说明 12、间距 6、小按钮；滚动条常规滑块宽 7、圆角 3.5、距边 2；悬停宽 11、轨道宽 15、描边 0.5；静止 1s 后淡出 | `UiCanvas/AcTextTertiary/UiDanger/AcScrollBrush/AcScrollHover/AcScrollTrack/AcHairline`；深色滚动条 40% 白，悬停 50% 白 |

标准控件通过 `.ac` 接入，亦可直接指定具名 Theme；独立项目只依赖 Avalonia 与 Themes.Simple。类名 `ac-hover/ac-pressed/ac-focus` 为预览提供确定的状态快照，和真实伪类共享同一资源与样式。皮肤资源覆盖见 `src/AvaMedia.Desktop/Styles/ComponentSkins.axaml`；Mac OS 9 / XP 的标准输入继续使用经典模板，组合组件使用同名资源覆盖圆角、颜色和阴影。预览页允许明确标注的示例数据，实际窗口仍只显示真实数据。浮层材质以主题半透明画刷实现跨平台回退。

#### 7.9.2 进度与阶段

- 确定和不确定进度均为 4px 条形。不确定条为 1.2s 往返滑块，行内禁止用环形替代条形；百分比放在标题或进度右侧，使用等宽数字，不叠在进度条上。旋转器为 0.8s 周期；减少动态效果由继承属性 `ComponentMotion.Reduced` 停止旋转与往返，保留静态进度。
- 步骤横向等宽，均高 64，说明最多两行省略并提供完整 tooltip；步骤间 12px 灰色箭头居中。右上计时、计数使用 `UiNumericFont` 和 `tnum`。
- 分类进度窗口只在标题显示百分比，阶段卡片下不添加独立进度条。子步骤的进行中弧形仅表示该子步骤活动。
- 视频总结任务窗口默认 960×700、最小 760×520；阶段进度占满摘要与底部操作之间的剩余高度，随窗口拉伸，超出内容在进度区内滚动，不使用固定高度的小视口或外层嵌套滚动。长视频分段进度保留独立的受限列表。

参考代码：[StageIndicator.cs](src/AvaMedia.Controls/StageIndicator.cs)、[Surfaces.cs](src/AvaMedia.Controls/Surfaces.cs)、[SegmentedControl.cs](src/AvaMedia.Controls/SegmentedControl.cs)。

```xml
<Button Classes="ac secondary" Content="临时停止" />
<Button Classes="ac danger" Content="结束任务" />
<ProgressBar Classes="ac" Value="33" Maximum="100" />
```

```csharp
var card = new AvaMedia.Controls.Card { Child = content };
var stage = new AvaMedia.Controls.StageIndicator();
stage.Update(1, "逐个文件分析", "按添加顺序处理文件", "412 / 1284", "running");
```

#### 7.9.3 分类媒体内容与列表

- 预览使用 `MediaPreviewSurface` 的统一投影；背景 `AcPreviewFill`、圆角 10，交互预览关闭氛围光。检测标签用白底胶囊、`UiText` 类别和 `UiAccent` 置信度文字，细投影；场景标签为半透明深色底与白字，不使用蓝色置信度块。
- 真实候选画面等宽排列，缩略图圆角 6、当前项 2px 强调色框。服装主色比例条高 `AcColorBarHeight` 8、圆角 4，说明置于下方；实际采样色允许直接显示。归组理由浅蓝底，左侧强调色半粗标题，正文允许换行。
- 文件流单行 8 个真实缩略图，已完成右下角 14px 绿色圆形勾，当前 2px 蓝框，等待透明度 `AcQueuedOpacity` 0.4；不补假数据。
- 分组采用通用列表行：40px 叠放缩略图和右上蓝色 `+N` 胶囊、两行信息、右侧 20px 粗体计数与“个文件”。组数增加后在卡片内滚动，不能挤压末行。只有真实有变化才显示 `+N`。
- 分类与字幕等任务共用 `JobRowView` 的行模板、状态胶囊和 4px 进度，入口按钮统一为“查看任务”。

实现引用：[分类进度窗口](src/AvaMedia.Desktop/Classification/ClassificationRunWindow.Run.cs)、[文件流](src/AvaMedia.Desktop/Classification/ClassifyWall.cs)、[任务行](src/AvaMedia.Desktop/Controls/JobRowView.axaml)。参考 `/workspace/AvaMedia-sidebar-review/**/demo-*.png` 与对应 HTML，以及 `ai-progress/v2/progress-from-tasklist-desktop.html`；该 HTML 中说明文字依真实数据变化，不能为匹配截图伪造分析结果。

#### 7.9.4 宿主接入与预览

桌面通过 `Styles/ModernComponents.axaml` 将普通按钮、选择、输入、滑块、进度和滚动条接到通用主题；专用导航、传输与窗口按钮保留角色模板。`Ui` / `ClassifyUi` / `SpeechUi` 只负责选择角色和内容，字幕提交/进度卡片改用 `Card`。`Motion.cs` 向组件传递减少动态效果偏好；`ScrollVisibility` 在停止活动 1s 后淡出覆盖式滚动条。经典皮肤仍由 `Platinum*.axaml` / `WindowsXP*.axaml` 提供标准控件，`ComponentSkins.axaml` 覆盖组合表面和状态令牌。

`tools/AvaMedia.AiProgressCapture --gallery-view` 打开可操作、可切换浅深色的 12 类组件预览；`--gallery` 截取所有区块。截图使用 zh-CN、DISPLAY=:11、AVAMEDIA_CHROME=mac、XDG_DATA_HOME=/tmp/avademo/data，输出到 `/workspace/AvaMedia-sidebar-review/components/impl/`；`compare_components.py` 生成左 demo / 右实控件的 `compare-s01..s12-light/dark.png`。整体 gallery 与分类窗口另交付到 `ai-progress/v3/`。

#### 7.9.5 空状态与错误状态（补充 9.3）

- 分组/结果尚无内容：居中 40px 状态符号和一句说明，不留空白。
- 单项失败：本行红色状态符号和真实原因，卡片保持原表面颜色。
- 任务失败或取消：标题百分比旁显示红色/灰色状态胶囊，隐藏暂停与结束按钮，只保留关闭/查看日志；已完成状态保持结果页操作。


#### 7.9.6 标准容器与独立切换（补齐）

- 独立 `ToggleButton.ac` / `AcToggleButton` 复用按钮高度、圆角与内边距；未选中使用 Fill，选中和混合使用强调色与白字，保留悬停、按下、焦点和禁用状态。
- `Expander.ac` / `AcExpander` 复用 Simple 的四向展开及内容模板，标题最小高 `AcDisclosureHeight` 28、字 `AcBodyFont`；线性折叠箭头宽 `AcDisclosureSize` 14。`TreeView.ac` / `AcTreeView` 与 `AcTreeViewItem` 使用同一箭头，层级缩进由资源 `AcTreeIndentConverter` 提供（默认 16）；行最小高 `AcTreeRowHeight` 28、圆角 `AcFieldRadius`，子节点保留原树选择、键盘导航和按需生成。
- `TabControl.ac` / `AcTabControl` 与真正的 `TabItem` 主题 `AcTabItem` 保留四边标签布局和 SelectedContent/SelectedContentTemplate。顶/底标签按 demo：字 `AcBodyFont` 13、标签间隔由 `AcTabMargin` 提供（右侧 22）、底留白 `AcTabPadding` 9、选中指示条 `AcTabIndicatorHeight` 2；左/右布局沿外侧显示指示条。ListBox 标签导航的主题另名 `AcTabSegmentItem`，不与 TabItem 混用。
- `ListBox.ac` / `AcListBox`、`AcListBoxItem` 提供滚动及虚拟化容器。默认文本行最小高 `AcTextRowHeight` 36，`.media` 为 `AcListHeight` 56，允许内容撑高；圆角 `AcListRadius` 8、内边距 `AcListPadding` 0,10、行间距 `AcListMargin` 8。悬停 Fill、非活动选择 AcSelection、活动选择强调色与白字；焦点使用 AcFocusShadow，禁用降低透明度。`.surface` 包装已有 ListRow 时清除容器内边距、最小高和间距，避免双重表面。分段导航和专用宿主列表不自动套用普通列表主题。
- 数值框默认总最小宽 `AcNumericMinWidth` 91（72 字段 + 4 间距 + 15 步进器），`.compact` 最小宽 `AcNumericCompactMinWidth` 64；宿主可以动态覆盖令牌，不在业务工厂中固定数值框列宽。
- 新主题通过 `.ac` 选择器排除 `mac-os9` / `windows-xp`。两种经典皮肤继续采用宿主原生标准控件模板；现代浅深色使用既有 Ac/Ui 颜色令牌，不另建调色板。专用设置导航、主工具树和窗口菜单保持各自角色模板。

#### 7.9.7 原生浮层材质与回退

- Avalonia 固定为 11.3.22：macOS Native 的 `AcrylicBlur` 映射到原生 Blur 模式，后端补偿级别为 0，可将 `ExperimentalAcrylicMaterial` 与原生背景模糊组合。这里不使用单独的 `Blur` 请求（该版本 Native 映射只接受 AcrylicBlur）。源码依据：[TopLevelImpl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Native/TopLevelImpl.cs)、[WindowBaseImpl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/native/Avalonia.Native/src/OSX/WindowBaseImpl.mm)、[ExperimentalAcrylicBorder](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Controls/ExperimentalAcrylicBorder.cs)。
- `MaterialSurface` / `AcMaterialSurface` 用于组件下拉、ToolTip 和 Popover 的背景。只在 macOS 原生 `PopupRoot` 请求 `AcrylicBlur, Transparent`；背景透明，并仅在 `ActualTransparencyLevel` 实际返回 AcrylicBlur/Blur 时启用材质。每个表面独占材质实例，避免不同浮层互相改写平台补偿。
- 原生弹出窗口必须清除 AppKit 窗口背景，并将原生模糊视图的位置、尺寸和圆角对齐到 MaterialSurface；仅设置 Avalonia 背景透明或内层 Border 圆角会露出矩形底。只裁切模糊视图，内容与投影独立绘制；关闭浮层时恢复原生状态。无法取得原生模糊视图或使用非均匀圆角时请求透明窗口并使用主题画刷回退。
- `AcMaterialEnabled` 默认 true；`AcMaterialTintColor` 浅色 #F6F6F8、深色 #2C2C30，`AcMaterialTintOpacity` 0.8、`AcMaterialOpacity` 0.5。Mac OS 9 / XP 关闭材质；不支持的平台、内嵌和 overlay 浮层仍使用 `AcMenuFill` 半透明画刷。关闭或拆卸表面时恢复弹出宿主的原透明度请求与背景。
- Avalonia 11 的此路径模糊原生浮层背后内容，不能直接模糊同一窗口内的兄弟控件。不把完整业务窗口设为模糊，也不把 Linux 的半透明截图描述为 macOS 原生材质验收。窗口内菜单栏和 NativeMenu 使用自己的主题，未在本轮接入范围内。
---

#### 7.9.8 公共组件与宿主资源契约

| 角色 | 动态令牌 / 默认 | 宿主使用 |
| --- | --- | --- |
| Card / SectionHeader | `AcCardRadius=12`、`AcCardPadding=16`、`AcCardFace/AcCardShadow`；`AcSectionFont=13`、`AcSectionMargin=0,0,0,8` | 内容容器用 Card；窗口专项在自己的资源字典覆盖，不修改全局默认。 |
| 字段 / 错误说明 | `AcFieldHeight=24`、`AcFieldRadius=6`、`AcFieldPadding=8,0`、`AcFieldDisabledOpacity=0.45`、`AcMultilineHeight=76`、`AcFieldErrorMargin=0,6,0,0` | 字段 `error` 与真实错误说明并存；多行高度是最小值，正文可换行。 |
| 状态 | `AcStatusHeight=20`、`AcStatusRadius=10`、`AcStatusPadding=8,0`、`AcStatusDotSize=6`、`AcStatusGap=4` | `StatusPill` 的 State 驱动画刷；显示文字由调用方本地化，单区域只放一个状态。 |
| Sheet | `AcSheetRadius=16`、`AcSheetPadding=18,20,18,16`、`AcSheetFace` | 浮层使用默认圆角 / 阴影；顶层窗口正文加 `window`，清除额外圆角 / 阴影。Ui 确认及列表提示共用此角色。 |
| 空 / 错误态 | `AcEmptyIconSize=40`、`AcEmptyGap=6`、`AcEmptyDetailWidth=220`、`AcEmptyPadding=16`、`AcEmptyActionMargin=0,8,0,0` | 与列表同层；`IsError` 同时对应 `error` 类与失败符号，Actions 只放真实操作。 |
| 工具标题 / 位置 | `UiToolTitleBarHeight=48`、`UiToolTitleGutter=88`、`UiToolTitleLightInset=16,0,0,0`、`UiToolWindowGap=12` | WrapDialog 只显示一处窗口标题；不改主窗口 52 / 编辑器 44 或原生 DWM 策略。Skin 注册独立窗口的位置记忆。 |

- 原生浏览器等依赖能力必须在附着宿主前判断；不可用时用 EmptyState.error 替代，不以 `IsVisible=False` 掩盖仍会初始化的原生控件。Linux 下载窗口隐藏“网页视频识别”及“内嵌浏览器”登录态选项，解析失败不自动打开内嵌浏览器；链接解析、外部浏览器 CDP 识别及其登录态选项继续提供。Linux 不附着原生浏览器适配器，直接创建嗅探窗口时保留真实不可用原因及关闭操作；Windows 运行时缺失 / 能力查询失败同样兜底，保留入口以便安装运行时。公共宿主先保底，业务列表 / 重试及完整接入在专项任务完成。
- 加载顺序：组件 Defaults / ControlThemes → UiStyles → PlatformTokenLayer → 宿主角色 / ComponentSkins → 窗口专项与经典皮肤。窗口局部资源覆盖同名 Ac 键仅影响该窗口；新专项样式先写入本规范。

#### 7.9.9 圆角表面与内容绘制

- 按钮、列表和分段的状态底色只绘制在外层表面；组件模板内容区标记 `ac-content` 并保持透明，宿主 SimpleTheme 专用状态样式排除该内容区。胶囊按钮与编辑器帧按钮的专项底色同样作用于外层表面。
- 圆角表面通过 `SurfaceContentClip` 裁切子内容，保留已有内容裁切与外层投影；分组首尾行、通知色条和零内边距卡片不得越过圆角。更新圆角、布局或替换内容后同步裁切，拆卸时恢复子内容原状态。
- MaterialSurface 的投影与内容裁切分层；气泡箭头放入真实布局空间，不能用负外边距画到原生弹窗边界外。

## 8. 窗口与标题栏（按平台）

- 统一通过 `MainWindow.WindowChrome`（`src/AvaMedia.Desktop/MainWindow.WindowChrome.cs`）和 `Controls/WindowChrome.cs` 复用，不另写标题栏。
- **macOS**：客户区延伸到标题栏下方（主窗口标题行高 52）。使用 `NSWindow.standardWindowButton` 提供的原生红绿灯，保留系统尺寸、按钮间距、悬停 / 失焦及全屏行为，不重绘或替换按钮。红绿灯与实际标题行 / 右侧工具按钮共用垂直中心；位置使用逻辑点，不乘 Retina 缩放。主窗口红绿灯在侧栏卡片左边缘内缩 `UiMainTitleLightInset=20`，当前窗口左边距为 28pt；工具树使用独立的固定标题留白，滚动时不进入按钮区。工具及编辑器窗口左边距使用 `UiToolTitleLightInset` 的 Left（16pt），编辑器标题内容保留 `UiToolTitleGutter=88` 的按钮区域。打开、布局 / 尺寸 / 缩放变化、激活及退出全屏后统一重新对齐；全屏和经典皮肤交给系统。非 macOS 的 `AVAMEDIA_CHROME=mac` 仅绘制同位置的预览占位。
- **Windows**：保留 `SystemDecorations.Full`，扩展客户区并使用 `NoChrome`；最小化 / 最大化 / 关闭按钮位于**右上角**标题行，按钮 46 × 32、Lucide 12 px 图标，关闭悬停为 `#C42B1C` 红底白图标；最大化后图标切换为还原。Win11 显式请求 DWM `DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND`，阴影由系统提供；Win10 保留原生直角与阴影，不伪造 Win11 圆角。最大化、全屏、贴靠时形状由系统决定。
- **Linux**：主窗口、编辑器及所有工具窗口统一保留 `SystemDecorations.Full`，由窗口管理器和合成器提供边框、缩放及阴影；不单独去掉某个工具窗口的装饰。圆角、阴影能力取决于桌面环境。`AVAMEDIA_CHROME=mac` 只预览客户区布局，仍保留 Linux 原生装饰。
- **整窗表面**：所有 `Window` 子类及临时 `new Window` 通过 `Skin` 的统一注册接入 `Controls/WindowChrome.cs`；已有主窗口、编辑器标题布局也调用同一平台策略。Windows / macOS 整窗使用不透明主题背景及 `TransparencyLevelHint=None`；禁止用透明整窗 + 自绘圆角边框或阴影替换系统形状。macOS 保留原生框架、圆角和阴影；内部卡片圆角不代表窗体圆角。
- **独立顶层窗口**：所有非模态工具、设置、帮助、播放器、看图及封面预览统一使用 `WindowChrome.ShowIndependent`（内部无 Owner 的 `Show()`）并设置 `ShowInTaskbar=true`，启动位置按 1.3 优先主窗口旁或恢复最近位置；显式 Manual 坐标保留；主窗口最小化或隐藏时工具窗口继续显示，可在任务栏 / Dock 的窗口列表独立切换。不得为层级感设置 Owner、置顶或用 `ShowDialog` 打开非模态工具；层级感只由系统原生阴影体现。确实需要返回确认 / 编辑结果的模态提示和编辑表单可保留 `ShowDialog(owner)`，同样复用整窗外观策略。业务上下文显式传递或从应用主窗口取得，不能依赖工具的 Owner 链。
- 窗口定位统一使用桌面坐标：现有窗口的边界尺寸乘 `DesktopScaling`，目标屏幕上的尺寸和间距乘该屏幕的 `Scaling`；不得将用于绘制的 `RenderScaling` 混入坐标计算。macOS Retina 的桌面坐标仍为逻辑点。未显式设置启动位置的窗口也接入公共定位，不能将默认枚举值 Manual 当成手动定位请求。
- 标题行和队列栏可拖动窗口，双击切换最大化；按钮、输入框、菜单、滑条、下拉、选择控件和分隔条不触发拖动，禁用控件的区域也不作为拖动留白。主窗口、编辑器和工具窗口统一通过 `WindowChrome.HookDrag` 在窗口隧道路由处理标题输入；顶部标题带覆盖客户区上边距和两侧留白，队列栏只覆盖自身行及外边距。全屏禁用标题拖动；标题拆卸时移除监听，原生拖动开始前释放隐式指针捕获。平台原生移动由 `BeginMoveDrag` 提供，不自行逐帧改写窗口位置。
- `AVAMEDIA_CHROME=mac|win|system` 可覆盖平台检测；`system` 及 Mac OS 9 / XP 皮肤使用各自框架。
- Mac OS 9 使用 `PlatinumWindowFrame`（关闭 / 缩放 / 收起标题栏按钮、标题横纹、右下缩放块）；XP 使用 `WindowsXPWindowFrame`。经典框架是客户区皮肤，外层同样保留系统装饰和不透明表面，不以 `SystemDecorations.None` 牺牲系统阴影。

参考代码 `src/AvaMedia.Desktop/MainWindow.WindowChrome.cs`：

```csharp
internal static WindowFrameKind FrameKind => Environment.GetEnvironmentVariable("AVAMEDIA_CHROME")?.ToLowerInvariant() switch
{
    "mac" => WindowFrameKind.Mac,
    "win" or "windows" or "linux" => WindowFrameKind.WinLinux,
    "system" => WindowFrameKind.System,
#if MACOS
    _ => WindowFrameKind.Mac
#else
    _ => OperatingSystem.IsMacOS() ? WindowFrameKind.Mac : WindowFrameKind.WinLinux
#endif
};
```

当前平台实现见 `Controls/WindowChrome.Platform.cs`：保留 `SystemDecorations.Full` 和不透明背景，Windows / macOS 扩展客户区；Linux 保留 WM 装饰，mac 预览只改变客户区布局。不要复制去掉系统装饰的旧参考片段。

macOS 原生按钮定位见 `Controls/WindowChrome.Mac.cs`：由共享入口读取实际标题行中心，必要时扩展原生标题宿主的点击范围，保留系统按钮视图层级。主窗口固定标题留白及非 macOS 占位见 `MainWindow.WindowChrome.cs`；占位只在预览模式显示，位置与原生布局使用同一基准。

`src/AvaMedia.Desktop/Controls/WindowChrome.cs`（右上角按钮与拖动）：

```csharp
panel.Children.Add(Caption("minus", "最小化窗口", () => window.WindowState = WindowState.Minimized));
panel.Children.Add(maximize = Caption("square", "最大化", () => ToggleMaximize(window)));
panel.Children.Add(Caption("x", "关闭", window.Close, close: true));
```

```csharp
public static bool IsInteractive(Visual? visual)
{
    for (var c = visual; c is not null && c is not Window; c = c.GetVisualParent())
        if (c is Button or TextBox or Menu or MenuItem or RangeBase or SelectingItemsControl
            or AutoCompleteBox or DatePicker or TimePicker or Calendar or GridSplitter or SelectableTextBlock) return true;
    return false;
}

/// <summary>Drag the window from a title region; double-click toggles maximize.</summary>
public static void HookDrag(Window window, Control region, Func<bool> enabled,
    Func<PointerPressedEventArgs, bool>? hitTest = null)
```

`src/AvaMedia.Desktop/Styles/MainShell.axaml`：

```xml
<Style Selector="Window.main-window Button.caption"><Setter Property="Width" Value="46"/><Setter Property="Height" Value="32"/><Setter Property="MinHeight" Value="32"/><Setter Property="Padding" Value="0"/><Setter Property="CornerRadius" Value="0"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Background" Value="Transparent"/><Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
<Style Selector="Window.main-window Button.caption.close:pointerover /template/ ContentPresenter#PART_ContentPresenter"><Setter Property="Background" Value="#C42B1C"/></Style>
```

`src/AvaMedia.Desktop/Controls/PlatinumWindowFrame.cs`：

```csharp
var close = CaptionButton("close", "关闭窗口", "PlatinumClose", () => window.Close()); titleBar.Children.Add(close);
var zoom = CaptionButton("zoom", "缩放窗口", "PlatinumZoom", Zoom, window.CanResize && window.CanMaximize);
var shade = CaptionButton("collapse", "收起 / 展开标题栏", "PlatinumShade", ToggleShade);
```

---

## 9. 状态

- AI 标签页的模型状态保留固定 18×18 图标槽位：加载用 4×4 点阵列扫描，预热用中心向外的轻脉冲，使用中用周边顺序点亮；点径 2、间隔 2、周期 1.2s，颜色引用 `UiAccent`，静态底点使用同色低透明度。点阵只表示活动；已就绪、失败和未加载分别用 Lucide `check`、`circle-alert`、`minus`。模型状态和任务阶段切换复用 `MotionNavigate` 的 160ms 淡入，不对每秒计时或采样计数重复播放，不等待动画再更新真实状态。模型准备与紧凑任务进度统一为 4px 条形：已知阶段总量显示真实比例，未知总量使用不确定条，暂停和结束时停止。减少动态效果及经典皮肤显示静态点阵；隐藏、最小化或拆卸时停止合成帧。
- 高频视觉效果优先使用 Avalonia 合成线程：模型活动指示只在运行或不确定准备阶段请求绘制帧，多宫格加载脉冲使用合成透明度动画。隐藏、最小化、关闭、拆卸和减少动态效果时停止动画；业务进度、节流和倒计时仍按原生命周期更新。
- 下载结果使用受限视口中的虚拟列表，保留复选框选择与行内操作，不增加行选中底色；相邻视口预缓存仅在此列表设为 `CacheLength=0.5`。压缩、瘦身和下载的类型明确的数据模板使用编译绑定，不全局打开动态绑定的编译模式。
- 视频总结封面与帧卡片按展示宽度乘屏幕缩放解码，保持实际画面比例；图片压缩原尺寸对比及媒体输出保留完整分辨率。

### 9.1 交互态

| 状态 | 规则 |
| --- | --- |
| 悬停 | 浅色叠加（`ShellCapsuleHover` / `ShellRowHover`，网页 `#E6E6EB`）；分类规则卡片悬停、按下保持卡片色彩 |
| 按下 | 主按钮 `UiPrimaryPressed`；网页按钮不透明度 0.8 |
| 选中 | 列表行浅蓝底 + 左侧 3 px 蓝条；工具胶囊、筛选胶囊蓝底白字；分段白底细阴影 |
| 焦点 | 键盘可见焦点（`:focus-visible`）必须可辨认；滑条键盘聚焦时显示小滑块 |
| 禁用 | 按组件角色：`AcDisabledOpacity=0.4`、`AcFieldDisabledOpacity=0.45`；主按钮灰底灰字 |

### 9.2 任务状态

| 状态 | 图标 / 颜色 | 可用操作 |
| --- | --- | --- |
| 运行中 | `loader-circle` 蓝 | 暂停、停止、详情 |
| 等待 | `clock` 灰 | 开始、暂停、停止、详情 |
| 已暂停 | `pause` 橙 | 恢复、停止、详情 |
| 已完成 | `check` 绿 | 详情 |
| 失败 | `circle-alert` 红 | 重试、详情 |
| 已取消 | `ban` 灰 | 详情 |

### 9.3 空状态与恢复

- 没有数据时显示简短空状态文字，不显示占位或假数据。
- 未确认的上次提交用顶部横幅（横幅色）提示“放弃 / 恢复”。

---

## 10. 专用界面

### 10.1 编辑器

参考 `docs/ui/editor-demo.png`、`editor-crop.png`、`editor-demo-mac.png`。

- 顶部：平台窗口外观 + 居中分段模式切换；快速剪辑仅有“剪辑 / 画面裁剪 / 自动转正”，经典编辑器保留“选项”。
- 中部左侧：大预览（黑底圆角 10）+ 传输控制（跳到起点、上一帧、播放、下一帧、跳到终点、时间码 `mm:ss.mmm / 总时长`）。
- 预览下方：胶片条时间线（缩略图由共享胶片条服务生成，可缩放），片段为蓝框，选中片段显示两端拖动柄；拖动柄时浮出该时间点缩略图和“起点 / 终点 时间码”。
- 右侧检查器：片段列表（小缩略图、区间、时长）、选中片段的“起点 / 终点”首尾帧卡片（时间码、前后一帧微调、跳转），以及按卡片分组的参数。
- 底部导出栏：左侧摘要，右侧主按钮。
- 所有已有功能和快捷键必须保留；快捷键只在 tooltip 中。
- **快速剪辑尺寸**：侧栏宽 330，片段行高 40，首尾帧高 75；时间字段为居中的 12 半粗 `mm:ss.fff`（超过一小时为 `h:mm:ss.fff`）。属性行高 34，值用 `#6E6E73`（`QcValue`），无框下拉加细灰箭头。时间线工具按钮高 30，标尺高 14，胶片条高 52，底部仅显示 11 px 次文字摘要；缩放轨宽 70、蓝色填充、白色阴影滑块。
- **快速剪辑结构**：三种模式始终显示公共标题栏、预览、传输控制、胶片条时间线与底栏。画面裁剪使用右侧检查器放置比例、精确坐标、重置与去水印选区；自动转正卡片追加在时间线下方，预览可适度缩小，卡片圆角 14、内边距上 12 / 左右与下 14，高约 148，保留方向胶囊、镜像、识别状态与操作。1280 × 800 下时间线不得被模式卡片替代。
- **底栏**：提供输出配置、下一个视频和进入导出选项。进入导出选项前，其他勾选的视频必须已有剪辑，否则保留编辑器与草稿，弹出“还有视频未剪辑”模态提示，逐行列出文件名，说明需先剪辑或取消勾选（对话框规则见 7.6）。
- **媒体信息**：标题栏文件名 / 副标题之后放置带提示与无障碍名称的小型媒体信息图标；悬停和点击均打开卡片式浮层，显示文件名、时长、分辨率、视频与音频编码。信息行高 32，标签灰色、值右对齐且数字等宽；长文件名省略并提供完整悬停提示。
- **选项归属**：不再显示快速剪辑选项页卡片。“输出配置…”打开完整片段设置弹窗，预览摘要区显示实时音频状态与输出说明；音量、静音、音频效果和字幕等仍在此处。旋转 / 镜像在自动转正可达，速度 / 视频淡入淡出在剪辑侧栏可达，裁剪与去水印选区在画面裁剪可达，均读写同一片段选项。
- 皮肤：深色使用 Qc 令牌；Platinum 保留原生浮雕按钮、原生控件外观及清晰文字。

参考代码 `src/AvaMedia.Desktop/Styles/QuickClipStyles.axaml`（Qc 令牌，节选）：

```xml
<SolidColorBrush x:Key="QcLine" Color="#E5E5EA"/><SolidColorBrush x:Key="QcRowLine" Color="#F0F0F2"/><SolidColorBrush x:Key="QcText" Color="#1D1D1F"/>
<SolidColorBrush x:Key="QcSecondary" Color="#86868B"/><SolidColorBrush x:Key="QcValue" Color="#6E6E73"/><SolidColorBrush x:Key="QcIcon" Color="#3A3A3C"/>
<SolidColorBrush x:Key="QcAccent" Color="#007AFF"/><SolidColorBrush x:Key="QcAccentText" Color="#FFFFFF"/><SolidColorBrush x:Key="QcSegTrack" Color="#E9E9EE"/>
<SolidColorBrush x:Key="QcSegOn" Color="#FFFFFF"/><SolidColorBrush x:Key="QcRowSelected" Color="#0F007AFF"/><SolidColorBrush x:Key="QcChip" Color="#F2F2F7"/>
```

`src/AvaMedia.Desktop/Styles/ControlRoles.axaml`（传输按钮）：

```xml
<Style Selector="Button.transport">
  <Setter Property="Width" Value="{DynamicResource UiTransportSize}"/>
  <Setter Property="Height" Value="{DynamicResource UiTransportSize}"/>
  <Setter Property="Padding" Value="{DynamicResource UiCompactButtonPadding}"/>
</Style>
```

### 10.2 AI 媒体整理提交窗口

- 紧凑窗口约 900 × 520，底色 `#FAFAFC`；两栏宽度比 1 : 1.75，栏间距 18；标题 15 粗，底部次级按钮和主按钮高 32。
- 分类规则为四列等宽卡片，高 46、圆角 10、间距 8；标题 13 半粗。选中为系统蓝底、白色勾选及标题、浅蓝说明；未选中为白底与 1 px 分隔线色边框。悬停、按下保持卡片色彩；更多规则在卡片区域滚动。
- 来源使用真实媒体组成 128 × 96 的 2 × 2 拼图，外圆角 10；下方为粗体名称与次色数量。输出目录卡片白底、圆角 10，蓝色文件夹图标及更改胶囊。
- 参数卡片标签 11 次色、数值 15 粗；轨道高 4，系统蓝填充。帧数用与当前帧数相同数量的短划线表示；保留滑条点击、拖动和键盘调整，悬停或键盘聚焦时显示小滑块。
- 复选框为 14 px 蓝色圆角方块；复制 / 移动分段使用灰底、白色选中段与轻阴影。

参考代码 `src/AvaMedia.Desktop/Classification/ClassificationSubmitStyles.axaml`（颜色随皮肤切换，见第 13 节）：

```xml
<Setter Property="Background" Value="{DynamicResource UiAccent}" />
<Setter Property="BorderBrush" Value="{DynamicResource UiAccent}" />
```

---


### 10.3 媒体预览的投影与氛围光（视频处理工具通用）

适用于所有带大预览区的视频、图片处理窗口，比如多宫格截图、剪辑、裁剪、去水印、压缩、转换、播放器封面区。

- **投影（必须）：** 预览图使用柔和的多层投影，例如 `0 1 2 rgba(0,0,0,.06)`、`0 8 24 rgba(0,0,0,.10)`、`0 24 48 rgba(0,0,0,.08)`。圆角跟随预览卡片（10 到 14）。不要使用硬边框代替投影。
- **氛围光（推荐）：** 从当前预览画面的边缘取主色，在预览背后画一圈大半径、低不透明度的光晕（不透明度 0.25 到 0.35，模糊半径不小于 60），颜色从画面边缘向外渐隐。画面变化时，颜色在 300 到 500ms 内平滑过渡。
- **取色性能：** 只对缩小到 64px 以内的位图取色，放在后台线程计算，按帧或文件缓存，不能阻塞预览刷新和交互。
- **何时关闭：**
  - 没有真实画面时（占位格、加载失败、纯音频）不显示氛围光，只保留中性投影。
  - Mac OS 9 和 XP 皮肤关闭氛围光，使用各自原有的样式。
  - 深色主题下降低不透明度，避免发灰。
- 超过 400 万源像素或 2MiB 编码数据跳过取色时保留中性投影，不把缺少氛围光判为缺陷。交互预览关闭氛围光。
- **实现：** 统一使用公共的预览容器控件，取色逻辑放在可单元测试的辅助类里，各窗口不要各写一套。实现完成后，在此处补上代码引用。

参考代码：

- 公共容器：`src/AvaMedia.Desktop/Controls/MediaPreviewSurface.cs`。投影在 Viewbox 外绘制，保持显示像素半径；氛围光半径 72、扩散 12、颜色过渡 400ms。没有真实帧立即隐藏光晕；经典皮肤跳过后台取色。
- 主题令牌：`src/AvaMedia.Desktop/Styles/MediaPreviewStyles.axaml`。`UiMediaPreviewShadow` 为三层中性投影，`UiMediaPreviewRadius` 为 10；氛围光透明度浅色 0.30、深色 0.18，经典皮肤投影为空并保留 0 / 3px 圆角。
- 后台取色与缓存：`src/AvaMedia.Desktop/Controls/MediaPreviewColorSampler.cs`。单路后台解码最长边不超过 64px，128 帧有界缓存，以缩略图内容摘要、文件、时间点和预览尺寸区分；取消过期任务，不读取 UI 持有的位图。编码数据超过 2MiB 或源位图超过 400 万像素时跳过氛围光，保留中性投影，避免昂贵解码。
- 可单元测试的纯计算：`src/AvaMedia.Core/MediaPreviewColors.cs` 的 `Sample` / `AverageBorder`。RGBA 边缘按 alpha 加权，四条外围边分别平均后等权合并；透明边和未完成的格子不参与，内部格子不取色。
- 多宫格接入：`src/AvaMedia.Desktop/ContactSheetWindow.Preview.cs`，逐帧提交已经取得的缩略图字节，后台颜色完成后独立更新光晕，不等待取色再显示预览帧。
- 交互预览流畅优先：`MediaPreviewSurface.UseAmbientLighting=False` 只绘制中性投影，不创建取色解码器、不提交后台取色或逐帧光晕更新。`FitContent=False` 保持裁剪和图片对比视口的原有尺寸与坐标；默认模式包在 Viewbox 外，投影跟随适应后的画面尺寸。
- 推广接入：`EditorWindow.axaml` 的主预览、`BatchCropWindow.axaml`、`BatchRotateWindow.axaml`、`ImageCompressionWindow.axaml`，以及 `Controls/MediaPreviewPanel.cs` 的视频 / 图片视口。共享面板的音频波形、压缩包 / 目录列表不加；`ClipExportWindow` 仅有小缩略图，播放器没有独立封面区且直接呈现连续播放画面，均不加，播放器全屏保持原样。


### 10.4 视频预览框比例

- 所有视频预览框（工具窗口预览、任务详情预览、剪辑 / 裁剪 / 转正预览等）随可用区域伸缩，不固定为 16:9。画面按原比例等比放入（不裁切、不拉伸），并**始终至少铺满一轴**（贴齐上+下，或贴齐左+右）。禁止画面缩在中间、四边都留空。
- **推荐（空间允许时）：** 预览容器收缩为与片源相同的宽高比，在可用区域内居中（框随片）。设置最大高度（建议不超过内容区高度的约 55%–65%，并给任务列表留位），竖屏或超宽片先限高 / 限宽再居中，避免挤掉右侧设置或底部任务条。
- 外框投影与氛围光按 10.3，**跟随视频内容边缘**，不画在空白底板或固定空框上。
- 图片按自身比例，PDF 纸页、双页 / 全屏 / VR 舞台与纯音频按 10.12 的专项边界。交互 `FitContent=False` 只保留外视口几何，不豁免内容等比和叠层坐标规则。
- 文件名 / 标题默认不叠在预览上；需要时悬停淡入，或放到窗口标题栏 / 任务行。
- 工具窗（如视频压缩）保持预览左、设置右；任务列表始终可见，不随预览比例变化而隐藏。
- 例外：裁剪等需在完整画面上编辑时，可用区域仍按上述适配；裁剪框画在画面内容区域内，不要求外框固定 16:9。

### 10.5 人物检测任务详情（B 版）

- 依据 `/workspace/AvaMedia-sidebar-review/person-task/person-task.html?v=b`、浅深色图及 `?v=s` 状态图：左栏占内容宽度约 42%，展示真实首帧或所选片段代表帧、文件名、分辨率 / 编码 / 时长与唯一状态胶囊；下方为保留片段、保留时长、用时、检测帧数的 2×2 数字卡。数字和时间码使用等宽数字。缺帧时只显示中性“暂无预览”空状态，投影复用 10.3 的公共预览容器。
- 右栏上方为全长时间轴、图例和可横向滚动的片段缩略图条，选中片段以强调色描边；时间轴悬停提示放在条下方，不能遮挡图例。下面是可滚动片段列表，列表末尾的处理记录默认折叠一行，展开查看完整日志。
- 进行中使用真实进度、采样帧数和部分结果增量填充时间轴；未检测部分不能视为已移除。失败显示红色原因并保留中断处及部分结果，不显示调整片段按钮。停止、暂停及等待保留真实队列操作。
- 底部左侧新建任务 / 重新执行，右侧关闭 / 唯一主按钮调整片段，进行中右侧提供停止。全部计数与片段来自任务结果；没有结果显示“—”或简短空状态。缩略图仅加载可见项，后台解码、可取消、复用媒体缓存，不阻塞 UI。皮肤颜色与圆角复用 Ui / AvaMedia.Controls 主题令牌。

### 10.6 播放器单帧导出

- “截取当前帧”保留在播放器控制栏与右键菜单中，Ctrl+E / Cmd+E 写在 tooltip；不建立独立窗口、页面或队列任务。按钮沿用现有 `player-transport` 角色，保留无障碍名称与按真实画面状态启用的行为。
- 保存当前显示画面为 PNG，VR 保存当前视角，输出到视频源目录；名称含播放时间，重名时添加序号，成功与失败使用现有播放器通知。
- 实现：`PlayerWindow.FrameCapture.cs`、`PlayerWindow.axaml`、`PlayerShortcuts.cs`。原生播放沿用 `Player/avamedia-shortcuts.lua` 与 `NativePlayerRunner` 的截图参数；编辑预览、AI 分析与多宫格的共享抽帧继续保留。

### 10.7 多宫格截图窗口（A 版）

- 默认窗口 1252 × 900，较小屏幕设置栏允许滚动。三栏：视频卡片宽 236、中间预览自适应、设置栏宽 312；栏间距 12。设置为“宫格 / 取帧 / 画面 / 输出”四组白色卡片，属性行高 34，紧凑数值控件保留直接输入与步进。
- 视频行含勾选、真实缩略图、文件名、时长和状态；导入、移除、包含子文件夹固定在列表底部。预览卡片顶部为文件名与像素尺寸，下方胶片条可拖动两端，重置范围使用链接。
- 底栏依次为真实生成摘要、打开输出文件夹、关闭、唯一主按钮“高清导出”；提交后进入主窗口持久任务列表，窗口内预览及参数仍可操作，关闭工具窗口不取消后台导出。任务的暂停、停止与重试复用任务列表。
- “画面”组增加“文字字体”通用 `ComboBox.ac`（AvaMedia.Controls `AcComboBox`）：默认（跟随界面字体）、Inter、Noto Sans SC、Fusion Pixel；选择立即记忆，作用于文件名、视频信息与帧时间码。Inter 的中文回退使用内置 Noto Sans SC；内置字体导出从 avares 加载，不按系统同名字体查找。字体菜单自身保持界面字体，控件宽 190；导出倍率下拉框宽 76。
- “画面”组提供“显示视频信息”，输出顶部文件名及视频尺寸、时长、帧率；预览保持相同文字字体。“输出”组提供 1× / 2× / 3× / 4× 导出倍率（默认 3×）与“轻微锐化 / 避开模糊帧”开关。避开模糊帧只在紧邻帧明显更清晰时替换，时间码标注实际选择的时间，默认关闭。倍率仅增加最终像素，预览按小尺寸快速抽帧；超过 4000 万像素时提示调整参数。图片导出文字与帧时间码随倍率同比增加，属性行复用现有布局与令牌，不使用 emoji。
- 多宫格预览允许以当前参数展示结构：无视频时显示按单格宽高比例排布的浅灰圆角格、格内序号、“选择视频后显示预览”和输出尺寸。这是参数示意，不是假媒体数据；不画插图、emoji、虚线框。
- 抽帧逐格填入，尚未完成的格子柔和明暗过渡；单帧失败显示“无法读取”，整段失败保留占位格并显示错误说明。参数及范围变化防抖 300ms，并取消旧抽帧。
- 所有颜色引用现有 Ui 主题资源；尺寸及圆角由 ContactSheetStyles 提供。现代卡片圆角 14、缩略图及占位格圆角 7；Platinum 无圆角、XP 使用 3px 圆角，保留经典皮肤原生控件和文字。

### 10.8 应用内文件选择器

- 文件选择器直接恢复媒体选择器大改前的 Git 源码（`81f72f9` 的 `FileBrowserView.cs` 与 `FileBrowserView.Compact.cs`），保留原有布局和交互；不重新绘制、不增加 Simple 呈现分支。原版已有左侧常用位置与目录树、路径导航、普通名称 / 大小 / 修改时间列表，以及系统选择、取消和确认按钮。
- 桌面默认窗口沿用原版 880 × 600、最小 680 × 420。宿主只保留当前的共享 WindowChrome、非模态窗口、存储对象解析与选择记录接入；内容布局直接使用历史组件。嵌入端继续使用历史 Compact 呈现。
- 目录读取采用现有来源接口，不启动媒体网格、检查器、缩略图或自动媒体探测；系统保存窗口维持原有授权与覆盖确认协议。

### 10.9 窗口内工具栏菜单栏（B 版）

- 参考 `/workspace/AvaMedia-sidebar-review/menubar/menubar.html` 的 B 区域及浅深色截图；使用已有 52px 平台标题行，不另建标题栏，不实现 A / C 方案。菜单放在标题行左侧，标题居中；任务计数放在下方任务栏，为中文菜单和 Windows 窗口按钮留出空间。文字菜单高 26、字 13、左右内边距 10、间距 6、圆角 7。正常透明，悬停 Fill，展开 FillHover 与 Medium 字重，禁用使用三级文字色。
- 下拉菜单位于工具栏底边下方 4px，最小宽 220、内边距 5、圆角 10；行高 24、圆角 5、左内边距 6、右 10、勾选列 14、列间距 6。选择行使用强调色与强调文字色；禁用项使用三级文字色，分隔线 0.5、上下间距 5、左右缩进 10。子菜单沿用同一模板与右箭头；快捷键只在提示中显示。跨平台弹出表面使用 demo 玻璃色与页面底色合成后的实色（浅色 `#F5F5F8`、深色 `#2A2A2E`），避免没有背景模糊的弹窗透出任务文字。
- `Styles/ToolbarMenus.axaml` 提供 `Mb*` 颜色、尺寸、圆角与阴影资源；浅色 / 深色按 demo，Platinum / XP 沿用各自原生菜单主题。菜单和 macOS 原生菜单共用命令树，保留 ff28d81 的文件、编辑、窗口、帮助及其命令顺序；原有队列、显示、转换等操作追加到相应分组。
- macOS 原生菜单已导出时隐藏窗口内菜单，避免同一命令占据两处菜单栏；未导出时保留窗口内菜单保证命令可达。`AVAMEDIA_CHROME=mac` 仅预览窗口框架，不代表原生菜单已经存在。

### 10.10 设置窗口（v2）

- 依据 `/workspace/AvaMedia-sidebar-review/settings/settings-v2.html` 与浅深色参考图，分类顺序为通用、外观与声音、播放器、转换与性能、本地模型、在线 AI、远程管理、高级、关于。侧栏宽 230（含内边距），导航行高 30，分类间两处 10px 间隔；内容内边距 26，页标题 15 半粗，分组标题 12，属性行最小高 40、内边距 12，组圆角 11。沿用公共平台窗口框架，不复制窗口标题栏。
- 优先采用 AvaMedia.Controls 的分段控件、分组表面和状态胶囊；属性开关在行末，说明 11px 次文字；子行缩进 18，依赖项禁用但保留可见。CPU 预留和图片质量采用滑块与右侧数值，预热比例保留紧凑步进器。
- 不设置访问令牌，局域网开启时提示“服务不要求令牌，只在可信网络中开启”；地址与客户端只显示真实数据。Beta 默认开启。模型管理保留独立的可达入口。
- 线程选择含自动及原有 1–16 的完整值；旧配置保持原来的明确线程数。更新方式合并为三项，未修改时保留旧标志。恢复默认只重置窗口显示的偏好，保留激活状态、检查时间及隐藏工具路径。
- 主题新增跟随系统，强调色色板通过 Skin 覆盖已有 Ui / Shell / Qc / Avalonia 主题资源，全应用共享；色板预设为蓝、紫、粉、红、橙、黄、绿、灰，经典皮肤保留其原生强调色。
- 内部工具在未激活时不显示入口、激活区域或操作提示；仅“关于”应用图标的隐藏手势打开口令弹窗。弹窗复用 Ui 的 Sheet、输入与对话框按钮，口令明文输入且不显示示例；验证成功后持久保存状态，视频侧栏显示内部工具分组并按真实可见工具计数。公开文档不记载口令，公共 Core / 网页目录不增加私有入口。MCP 仅在已解锁工作区注册内部 VL 预览和撤销接口，不提供口令或激活接口；未解锁时工具列表不显示内部接口。
- 内部 VL 命名分析通过现有共享任务队列提交，工具窗口保留原新旧名称表格，用“后台 VL 分析 / 后台规则分析”和“查看后台任务”操作；关闭工具窗口不停止已接收任务。主队列显示真实逐项进度、所选模型和错误；从任务列表或任务详情重新打开同一份预览，部分成功项可取消其他项后提交。编辑名称与勾选保存在分析任务中；确认后创建独立的事务重命名任务，日志及撤销入口仍走现有文件预留和恢复规则。不为私有任务新增公共工具目录入口。

### 10.11 天池独立媒体窗口

- 天池播放器与天池看图共用 `WindowChrome.WrapDialog` 平台标题栏及独立窗口行为；非模态、无 owner，主窗口任务区保持原布局并可操作。每次从外部入口打开均新建窗口；窗口内部的文件选择与拖放替换当前内容，连续定位片段的结果窗口可复用其播放器。
- 两者默认 1120×780、最小 760×500；各自记忆最近的普通窗口尺寸，最大化、最小化和全屏不覆盖尺寸。首次打开在来源窗口所在屏幕居中，已打开窗口恢复最小化后激活。
- macOS 保留原生红绿灯与独立全屏空间；全屏隐藏共享标题栏，退出恢复先前窗口状态。播放器保留快捷键、播放列表、VR 和截取当前帧，快捷键仅写在 tooltip。控制栏采用 AvaMedia.Controls 的传输按钮、滑块、下拉与菜单主题。
- 播放画面按原比例等比缩放、至少铺满一轴，留主题底色；全屏沿用等比播放，不添加静态预览光晕或投影。文件名仅显示在窗口标题栏。

### 10.12 阶段、纸页与媒体舞台边界

- 普通 StageIndicator 默认 `AcStageHeight=64`；字幕 `SpeechStageCard` 的 160 高摘要 / 波形舞台为专项角色，由字幕专项令牌提供，不能让通用阶段高覆盖。
- PDF 预览按真实纸页比例展示，不套视频至少铺满一轴的规则；纸张本身与主题 Card 表面分开，参数使用通用字段契约。
- 图片按自身比例适配；双页看图、全屏、播放器 VR 由各自舞台控制几何。纯音频波形、小片段缩略图和连续播放画面不加静态预览氛围光 / 投影。普通图片大预览复用 10.3，视频交互叠层坐标按 10.4。

### 10.13 软件更新

- “关于 → 软件更新”使用现有设置分组表面，持续展示真实检查结果、目标版本、包大小、下载与准备进度、待安装状态及失败原因。更新方式、状态、操作分别成行；中文说明允许换行。间距引用 `StControlGap`，操作外边距引用 `StUpdateActionMargin`（4,2），说明滚动区最大高度引用 `StUpdateNotesHeight`（200），进度复用 `ProgressBar.ac`。更新说明默认折叠，按原文显示为可选择文本，不执行 Release 中的 HTML。
- 帮助菜单及 macOS 应用菜单提供固定的软件更新入口，标题随检查、下载和待重启状态变化；关闭或清除通知不影响状态。所有入口复用同一个更新器和状态，不重复启动下载。
- 每个状态卡只有一个主操作：下载更新、重试下载、重试安装或重启并更新；检查、停止下载、发布页和日志使用次级按钮。准备阶段使用不定进度，下载阶段只使用真实字节数计算百分比。
- 队列运行或任务编辑时禁用重启，并显示等待原因；关闭到托盘不会触发安装。主动重启通过正常保存及退出流程，独立守护确认安装成功后才重新打开应用；普通退出安装不自动重新打开。
- 静默更新只隐藏常规提醒，失败仍可见并可重试、打开日志；下一次启动根据安装记录及实际运行版本显示成功反馈。后台检查不抢焦点，长期驻留时每天检查一次。

### 10.14 批量重命名

- 工具集中的普通批量重命名采用两栏：左侧命名规则与所选规则参数，右侧文件及新旧名称预览。窗口外边距引用 `RenameWindowPadding`（18），左侧宽度引用窗口专项 `RenameRulesWidth`（320），排序控件引用 `RenameSortWidth`（180）；规则库由“添加规则”菜单进入，保存与载入放入规则菜单，保留规则启用、上移、下移和删除。
- 文件区采用第 7.3 节的发丝线列表；表头与行使用同一列定义、间距和内边距。新旧名称按 2:3 分配剩余宽度，变化部分用半粗文字区分；文件名可省略并提供完整提示，类型及状态文案允许换行。列表行使用 ListRow，行高、字体与菜单沿用平台 Ac/Ui 令牌，不额外增加表面、斑马纹或整行状态填色。
- 表头复选框支持全选、取消全选与混合状态，只控制是否参与重命名；列表选择用于“移除选中项”。文件菜单保留反选与刷新预览。规则参数标签在字段上方，数值输入、下拉与高级设置复用通用组件，关键词匹配与词库入口继续可达。
- 普通规则变化自动更新预览；更新时保留上次名称并显示“正在更新预览…”，旧方案立即失效，只有当前预览完成并通过校验后才启用重命名。停止或失败保留可核对的上次名称，不把旧名称预览当作可执行方案。
- 底栏左侧撤销，中部真实进度或错误，右侧仅一个主按钮“重命名 N 个文件”；数量来自当前可执行方案，停止只在可取消操作中显示。窗口标题与关闭沿用第 8 节公共框架。窗口专项布局资源在 `Styles/RenameStyles.axaml`，不另建颜色、字体或按钮模板。

## 11. 远程网页与手机

参考 `docs/ui/remote-*.png`。断点 760 px。

参考代码 `src/AvaMedia.Browser/ConsoleView.cs`：

```csharp
var mobile = Bounds.Width < 760;
```

### 11.1 桌面（≥ 760 px）

- 顶部为 logo、标题、连接状态胶囊、居中分段导航（所有任务 / 文件与任务下发 / 服务器）、“重新连接”。默认打开“所有任务”。
- 较窄桌面窗口保留 logo、连接状态与导航，收起品牌长标题；长地址与服务器信息值可省略，悬停显示完整内容。任务列表可用宽度不足 640 px 时采用紧凑任务行并将标题与操作分行，右侧详情仍保留；响应列表自身宽度变化，不改变 760 px 的页面断点。
- **新建任务**：三张编号卡片“1 选择文件 · 2 选择工具 · 3 输出与提交”，宽度约 1.35 : 1 : 1；底部操作栏放“包括子文件夹 / 立即执行”和主按钮；未确认的上次提交用顶部横幅提示“放弃 / 恢复”。
- **文件浏览**：返回 / 上一级 / 面包屑 / 刷新；“常用位置”只列有意义的位置（不显示 /proc、/sys、/dev 等）；行首复选框，点文件夹进入。不提供拖放区。
- **编辑器**：顶部为返回、文件胶囊、工具徽标；左侧舞台（预览、传输、时间线或工具专属底栏），右侧检查器。剪辑、裁剪、去水印、合并都调用真实接口（`/web/preview/frame|strip|boundaries|nudge`）。
  - 剪辑：多片段、首尾帧卡片、逐帧微调、拖动柄缩略图浮层。
  - 裁剪：比例胶囊（自由 / 16:9 / 9:16 / 4:3 / 3:4 / 1:1）、三分网格、输出尺寸。
  - 去水印：多区域（最多 16 个），每个区域可设出现时间；非当前区域以标签框显示。
  - 合并：缩略图行、拖动手柄排序并保留上下移按钮、按时长比例的色条。

### 11.2 手机（< 760 px）

- 大标题、底部标签栏（所有任务 / 任务下发 / 服务器）、卡片式任务、右下悬浮“创建任务”（大号主按钮，悬浮阴影 `0 6 16 #55007AFF`）。
- 新建任务为分步导航，底部固定“下一步”主按钮；编辑器上下排列。
- 步骤条按可用宽度三等分，文字可换行，不用固定最小宽度撑破窄屏。恢复横幅在手机上将操作移到正文下方，按钮按可用宽度换行。
- 页面外边距 16。

### 11.3 网页主题资源

参考代码 `src/AvaMedia.Browser/Ui/RemoteTheme.axaml`：

```xml
<SolidColorBrush x:Key="Blue" Color="#007AFF"/>
<SolidColorBrush x:Key="Fill" Color="#F0F0F3"/>
<SolidColorBrush x:Key="FillHover" Color="#E6E6EB"/>
<SolidColorBrush x:Key="Hairline" Color="#E5E5EA"/>
<SolidColorBrush x:Key="TextPrimary" Color="#1D1D1F"/>
<SolidColorBrush x:Key="TextSecondary" Color="#86868B"/>
<SolidColorBrush x:Key="ThemeAccentBrush" Color="#007AFF"/>
```

宿主页面 `src/AvaMedia.Browser/wwwroot/app.css`：

```css
html,body,#out { width:100%; height:100%; margin:0; overflow:hidden; }
body { font-family:system-ui,sans-serif; background:#f7f8fa; }
```

---

## 12. 文案与本地化

- 界面以简体中文为原文；中文原文作为键，英文在 `src/AvaMedia.Browser/Strings/Remote.json`（网页）和桌面词典 `src/AvaMedia.Desktop/Localization/Strings.en-US.json` 中。
- 文件名、路径、用户输入等用户数据标记为 `raw`，不翻译。
- 文案不含 emoji，不含快捷键键帽；快捷键写在 tooltip 文案中，如“撤销 (Ctrl Z)”。
- zh-CN 文案不得被裁切；评审截图使用 zh-CN。

参考代码 `src/AvaMedia.Browser/Strings/Remote.json`：

```json
 "1. 选择文件": "1. Choose files",
 "2. 选择工具": "2. Choose tool",
```

`src/AvaMedia.Browser/Ui/L.cs`：

```csharp
if (change.NewValue is string text && !block.Classes.Contains("raw") && _english.TryGetValue(text, out var translated) && translated != text)
```

`src/AvaMedia.Desktop/Localization/Strings.en-US.json`：

```json
  "还有视频未剪辑": "Some videos still need editing",
```

---

## 13. 规范内矛盾的处理记录

| 问题 | 处理 |
| --- | --- |
| 旧规范第 4 节“工具少时不提供搜索框”与第 8 节“工具数量少的列表不加搜索框”是有条件的禁止，新约束要求“不提供搜索框” | **已修复**：统一为无条件禁止（第 1 节）；设置、文件浏览、媒体路由、分类、图片浏览、字幕结果及标签 / 词库界面的搜索框已移除，保留类别与状态筛选。 |
| 旧规范“不使用 emoji 作为图标或装饰”只覆盖图标 / 装饰 | **已修复**：扩展为界面任何位置均不使用 emoji；输出路径与预览文案去掉装饰字符，齿轮与网站装饰改用共享 Lucide 路径。 |
| 规范系统蓝 `#007AFF`，而 `Styles/UiStyles.axaml` 中 `UiAccent` / `ThemeAccentColor` 仍为 `#5B5BFA`，`UiNavigationSelection` 亦为 `#5B5BFA` | **已修复**：浅色通用强调色、内置控件和导航选中态统一为 `#007AFF`，深色为 `#0A84FF`；同时迁移悬停、按下和浅蓝配套色，经典皮肤强调色不变。 |
| 网页横幅令牌为 `#FFF8E6`，`Palette.Banner` 实际为 `#FFF6E0`；成功文字令牌 `#248A3D`，`Palette.GreenText` 为 `#1F9D3A`；等待令牌 `#86868B`，`ShellWait` 为 `#8E8E93` | **已修复**：`Palette.Banner` 为 `#FFF8E6`，`Palette.GreenText` 与浅色 `ShellGreenText` 为 `#248A3D`，浅色 `ShellWait` 为 `#86868B`。 |
| “不叠加多层阴影”与主窗口卡片 `0 0 0 0.5 …, 0 4 16 -6 …` 双层阴影 | 明确 0.5 px 描边阴影视为边框，不计入“多层装饰阴影”（第 6 节）。 |
| 卡片默认与窗口专项圆角 | 通用 Card 以 `AcCardRadius=12` / `AcCardPadding=16` 为准；平台覆盖见 5.1，窗口专项值使用自己的令牌（6、10）。 |
| 手机大标题：规范 26，网页 `largeTitle` 为 28 | 桌面大标题 26，手机网页大标题 28。 |
| 强调色预设 | 已有八色预设及跟随系统主题，Skin 通过资源覆盖；经典强调色保留（4.2、10.10）。 |
| 部分代码仍硬编码颜色（如 `ClassificationSubmitStyles.axaml`、`Classification/ClassifyUi.cs`、`RemoteTheme.axaml` 中的样式） | **已修复（所列文件）**：提交样式与 `ClassifyUi` 及其调用界面使用动态主题资源，网页样式 / 模板也使用资源；新资源提供四种皮肤对应值，真实媒体采样色保留。 |
| 直接显示快捷键的键帽、播放器 / 看图帮助及菜单右侧提示 | **已修复**：删除键帽和提示行，帮助改为 tooltip，菜单提示仅在 tooltip；现有键盘命令与 HotKey 保留。全仓未发现虚线拖放框；图表虚线、流程连线与 XP 焦点框保留。 |






## 14. 界面审计与截图验收

- 默认只在深色、zh-CN 下截图；用户点名时以其要求为准。浅色作为主要设计目标不改变截图范围；未截图的皮肤不能视为本次验收通过。仅做必要截图，不扩展为功能回归或像素对比。
