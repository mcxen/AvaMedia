# AvaMedia UI 设计与 Motion 规范

版本：1.1 · 更新日期：2026-10-05 · 技术基线：.NET 8 / Avalonia 11.3.22

本文是 AvaMedia 通用界面规范，适用于主窗口、转换、输出配置、设置、媒体编辑及后续工具窗口。新增控件应遵循本文；已有控件按下述实现状态逐步统一。示例与资源名称均以本仓库为准。

## 1. 调研结论与设计方向

Avalonia 是跨平台 UI 框架，其视觉由主题、资源、样式和控件模板共同决定。官方提供 `SimpleTheme` 与 `FluentTheme`：前者适合定制，后者提供受 Microsoft Fluent 启发的控件外观，并支持 Compact 密度。[Simple Theme](https://v11.docs.avaloniaui.net/docs/basics/user-interface/styling/themes/simple/)、[Fluent Theme](https://v11.docs.avaloniaui.net/docs/basics/user-interface/styling/themes/fluent/)

本项目采用 **经典桌面工具布局 + 克制的 Fluent 交互反馈**：紧凑、明确、便于重复操作。继续使用已安装的 `SimpleTheme`，在其上统一资源与交互样式。这是 AvaMedia 的设计选择；下面的色值、尺寸与动效时间是项目定义的参数。

| 维度 | 通用规则 |
| --- | --- |
| 信息结构 | 菜单 → 工具栏 → 功能区 / 任务区 → 状态栏；内容优先 |
| 视觉 | 中性底色、蓝色交互强调、小圆角、清晰边界 |
| 密度 | 鼠标 / 键盘优先的紧凑桌面密度；预览区域保持足够面积 |
| 层级 | 通过字号、间距、背景与边框建立；阴影只用于浮层 |
| Motion | 解释状态变化；反馈快、位移小、可取消、可关闭 |
| 品牌 | 彩色媒体图标可保留；交互状态采用统一语义色 |

Fluent 建议通过有目的、自然、一致的动效解释界面变化，并为顶层切换使用短淡化效果；本项目据此选择淡入与局部按压反馈。[Fluent 2 Motion](https://fluent2.microsoft.design/motion)

## 2. 本次落地与实现边界

| 范围 | 状态 | 实现入口 |
| --- | --- | --- |
| 浅 / 深主题的语义色、字体、圆角、输入框尺寸 | 已落地资源 | `src/AvaMedia.Desktop/Styles/UiStyles.axaml` |
| 通用 Button、tool、tile、category、TextBox、ComboBox、TabItem | 已接入全局样式 | `App.axaml` → `UiStyles.axaml` |
| 主窗口表面、分隔、说明文字、状态栏主题色 | 已接入动态资源 | `MainWindow.axaml` |
| 应用级浅 / 深色切换 | 已接入；当前选择未持久化 | `MainWindow.axaml.cs` |
| 按钮悬停 / 按压色彩过渡、禁用透明度过渡、tile 按压缩放 | 已落地 | `Styles/Motion.axaml` |
| 主窗口 / 对话框内容淡入、分类内容淡入 | 已落地 | `Motion.cs`、`ShowCategory` |
| 减少动效开关与持久化 | 已落地；设置点“确定”后生效 | `SettingsWindow.cs`、`AppSettings.ReduceMotion` |
| Windows 系统动画偏好 | 已落地；打开 / 激活窗口及应用设置时重读 | `Motion.cs` |
| 其他平台的系统减少动效偏好 | 待接入；目前遵循应用开关 | 平台适配层 |
| 状态色资源 | 已定义；任务文字尚未全部绑定到对应颜色 | `UiSuccess` / `UiWarning` / `UiDanger` |
| 统一矢量工具栏图标、空状态、内联校验、全部快捷键 | 下文规定目标；未完整落地 | 各业务窗口 |
| 所有历史硬编码颜色、边距与编辑器绘制颜色 | 尚未全部迁移 | 转换 / 编辑 / 新增工具窗口及自绘控件 |

全局主题资源覆盖通用控件不等于每个历史局部样式都已适配深色。剩余白色列表、灰色提示、自绘时间轴等，应在后续修改相应窗口时迁移。下文“应”表示统一设计要求；完成情况以上表及验收记录为准。

## 3. 颜色与主题 Token

颜色资源随主题变化，应使用 `{DynamicResource ...}`。固定尺寸与字体使用 `{StaticResource ...}`。`ThemeVariant.Light` / `Dark` 分别提供下表的值；主题切换通过 `Application.Current.RequestedThemeVariant` 作用于整个应用。[Theme Variants](https://docs.avaloniaui.net/docs/styling/theme-variants)

| 资源键 | 浅色 | 深色 | 用途 |
| --- | --- | --- | --- |
| `UiCanvas` | `#FAFAFA` | `#202020` | 窗口背景、功能区 |
| `UiSurface` | `#FFFFFF` | `#282828` | 列表、菜单、普通面板 |
| `UiSurfaceRaised` | `#F4F4F4` | `#303030` | 按钮底色、表头 |
| `UiText` | `#202428` | `#F3F3F3` | 正文与控件文字 |
| `UiTextSecondary` | `#666666` | `#BDBDBD` | 路径、说明、辅助文字 |
| `UiBorder` | `#B9B9B9` | `#727272` | 输入 / 操作边界 |
| `UiDivider` | `#DDDDDD` | `#454545` | 装饰性分隔线 |
| `UiTileBorder` | `#D8D8D8` | `#515151` | 功能磁贴边界 |
| `UiHover` | `#E8F3FE` | `#193B52` | 悬停、预览占位表面 |
| `UiPressed` | `#D7EAFB` | `#244C66` | 按压背景 |
| `UiAccent` | `#0078D4` | `#60CDFF` | 交互强调、焦点、信息 |
| `UiAccentBorder` | `#46A8DE` | `#60CDFF` | 悬停 / 按压描边 |
| `UiStatusSurface` | `#595959` | `#191919` | 状态栏；配白色文字 |
| `UiSuccess` | `#107C41` | `#76D69C` | 成功 |
| `UiWarning` | `#8A5700` | `#F2C66D` | 待处理、警告 |
| `UiDanger` | `#B42318` | `#FFB4AB` | 失败、破坏性动作提示 |

- 颜色表达语义时，必须同时有文字或图标。运行状态写“转换中 42.0%”，失败写“失败 · 查看日志”。
- `UiDivider` / `UiTileBorder` 是低对比分隔色；不能独立承担键盘焦点、错误标识或关键操作边界。
- 图标内部的品牌色、真实媒体画面与裁剪蒙层可固定；文字、面板、输入框与状态颜色应使用语义资源。
- 常规文字以对比度 ≥ 4.5:1 为设计基准；关键非文字状态 / 焦点以 ≥ 3:1 为基准。按实际背景与状态组合检查，不能只检查调色板。[WCAG 文本对比度](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)
- 当前浅色正文 / 辅助文字配 `UiSurface`，深色正文 / 辅助文字配 `UiSurface`，是默认组合。强调色不应随意用作长段正文。

## 4. 字体、文字与图标

| 资源 / 层级 | 字号（DIP） | 字重 | 使用规则 |
| --- | --- | --- | --- |
| `UiFont` | — | — | `Microsoft YaHei UI, Segoe UI`；缺失字体由平台回退 |
| `UiFontTitle` / `.title` | 18 | SemiBold | 对话框标题、主要区块标题 |
| `UiFontBody` | 13 | Normal | 正文、按钮、字段标签、功能名称 |
| `UiFontCaption` / `.caption` | 12 | Normal | 说明、状态、辅助信息 |
| 任务名 | 13 | SemiBold | 每行主要识别信息 |
| 编辑器时间 | 14 | Normal | `UiNumericFont` 等宽字体，统一三位小数；详见第 14 节 |

- 每个区块最多使用一个标题层级；避免同一视图同时出现过多字号。
- 新增可读文字最低使用 12 DIP。任务模板中历史 11 DIP 元数据是待迁移项。
- 正文使用内容驱动高度与换行；标签、路径在紧凑列表中可省略，但需 Tooltip 或详情入口显示全文。
- 数字采用统一格式：进度 `42.0%`，编辑器当前位置、总时长、起止时间与区间时长均为 `HH:mm:ss.fff`，像素 `1920 × 1080`。小时为累计小时，不在 24 小时后回绕。
- 中文界面使用完整动作词：“添加文件…”、“打开输出目录”、“重试任务”。省略号表示操作将打开后续输入或选择界面。
- 通用操作图标使用同一套原创 / 获授权的矢量资源；建议 16 / 20 / 24 DIP 与一致笔画。功能插画允许 48–64 DIP。
- 当前工具栏存在 Unicode / Emoji 图标，其平台外观不稳定，属于迁移项。图标按钮应有 `AutomationProperties.Name` 与 Tooltip；优先显示文字标签。

## 5. 尺寸、间距、圆角与层级

所有布局尺寸采用 DIP，媒体裁剪坐标采用源媒体像素；两者不得混用。

| 项目 | 通用规范 | 当前情况 |
| --- | --- | --- |
| 间距刻度 | 4 / 8 / 12 / 16 / 24 / 32 | 新布局采用；历史值 3 / 9 / 14 / 18 等逐步迁移 |
| 窗口内容边距 | 16–24；默认 20 | 现有窗口多为 18 / 22 |
| 同组控件间距 | 8；紧凑工具栏 4 | 逐步统一 |
| 组与组间距 | 16–24 | 用留白表示关系 |
| `UiControlRadius` | 4 | Button / TextBox 已采用 |
| 小型预览边框 | 2–4 | 主窗口预览占位当前 2 |
| 普通边框 | 1 | 功能磁贴当前 2，保留其识别性 |
| `UiInputMinHeight` | 28 | TextBox / ComboBox 已采用 |
| `UiButtonPadding` | `10,5` | 已采用 |
| `UiInputPadding` | `6,4` | TextBox 已采用 |
| 普通操作目标 | 建议至少 28 × 28 | 触屏场景至少 40 × 40 |
| 面板层级 | canvas → surface → raised | 普通面板用表面 / 边界区分 |
| 阴影 | 普通列表与磁贴无阴影；浮层轻阴影 | 保留系统窗口 / 基础主题浮层行为 |

尺寸应由内容与容器决定。最小高度用于保证可操作性；不要给多行说明设置会截断内容的固定高度。

## 6. 布局与缩放

主窗口保留当前四层结构：25 DIP 菜单、55 DIP 工具栏、中部可伸缩内容、26 DIP 状态栏；初始 `1440 × 860`，最小 `1050 × 650`。左侧功能区初始 425 DIP，分隔条 4 DIP，右侧任务区填充剩余宽度。以上是现有布局参数，非所有窗口的统一固定值。

- 功能区使用四列磁贴，常规磁贴占一列，组合功能可占两列；纵向滚动，不产生横向滚动。
- 任务区固定预览列，来源与输出列伸缩；长路径省略，全文可查看。
- 表单采用“标签 + 输入 + 可选浏览动作”列；同一表单标签列对齐。输出配置长表单可滚动，确认 / 取消固定在底部。
- 对话框以 owner 居中；非必要不锁定窗口大小。减少窗口尺寸时优先滚动正文，底部操作始终可见。
- 编辑器优先分配预览空间；时间轴与坐标输入保持直接响应。预览黑底属于媒体表面，不跟随普通面板底色。
- 在 100% / 125% / 150% / 200% 缩放下检查文字、图标、焦点和裁剪手柄。空间不足时允许内容滚动或重新排列，避免按钮被窗口边缘遮挡。
- 原生标题栏遵循平台行为；若未来定制标题栏，必须保留拖动、系统菜单、最大化、键盘与屏幕阅读器支持。

## 7. 组件与状态

### 7.1 Button

| 变体 / 类 | 视觉与使用 |
| --- | --- |
| 普通 `Button` | raised 表面、1 DIP 边框、4 DIP 圆角；一般动作 |
| `Button.tool` | 透明背景、无常驻边框、`8,3` 内边距；工具栏动作 |
| `Button.tile` | canvas 表面、2 DIP 边框、4 DIP 内边距；功能入口 |
| `Button.category` | surface 表面、顶部分隔线、高度 24、`5,1` 内边距；紧凑分类入口 |
| 主操作（目标） | 同一对话框只突出一个，例如“确定”或“开始”；独立语义类实现后统一采用 |
| 危险操作（目标） | 动作文字明确；必要时确认；不能与普通操作共用无区别的图标 |

| 状态 | 必须表现 |
| --- | --- |
| Normal | 标签可读，操作区域明确 |
| PointerOver | `UiHover` 背景 + `UiAccentBorder` 边界；120 ms 过渡 |
| Pressed | `UiPressed`；磁贴允许缩放到 0.985；120 ms |
| FocusVisible | 即时显示基础主题焦点提示与 `UiAccent` 描边；不能只依赖 Hover |
| Disabled | `IsEnabled=false`；基础主题控制透明度，停止动作响应；无悬停 / 按压触发 |
| Busy（目标） | 保持文字与布局稳定，防止重复提交；可取消动作保持可用 |

焦点状态不改变边框厚度与布局。`.category` 的 24 DIP 高度是现有紧凑例外，触屏模式需增高。

### 7.2 TextBox / ComboBox / 数值输入

- 输入边界与标签始终可识别；Watermark 只做示例，不能替代字段名称。
- 文本框最小 28 DIP、圆角 4、内边距 `6,4`；ComboBox 最小 28、内边距 `6,3`。
- 下拉框必须区分当前选项、指针悬停、键盘焦点与禁用状态；弹出列表遵循基础主题。
- 数值标签写清单位、范围和特殊值，如“画面宽度（0 = 原始）”、“速度（0.25–4）”。显示本地化，内部解析按业务数据格式处理。
- 无效输入应保留原值，在字段旁给出可操作的文字提示；保存时将焦点移到首个错误字段。当前多处使用错误对话框，内联校验待迁移。
- 读写路径可浏览；只读日志可复制、换行、滚动，不伪装成可编辑表单。

### 7.3 CheckBox / Tab / 分类

- CheckBox 标签包含完整含义；整行标签可操作，选中状态不能只有颜色差异。
- 同层 Tab / 分类切换只做短淡化，不制造前进 / 后退关系。
- 当前 `.category` 点击后会重建功能网格；上方分类标题显示当前位置。
- Tab 头使用 `12,6` 内边距；选中、悬停、焦点、禁用视觉由基础主题提供。
- 切换分类 / Tab 不应触发媒体播放、转换或改变任务数据。

### 7.4 列表与任务行

- 每行包括预览、名称 / 来源、输出 / 进度 / 状态；首要信息加粗，辅助路径使用 secondary。
- 支持单选、多选与右键动作；选择视觉与悬停视觉必须不同，焦点提示独立存在。
- 右键菜单与双击动作应与当前任务状态匹配；正在运行的任务不能被错误地重新编辑。
- 输出路径省略时提供完整路径 Tooltip；不能把唯一错误原因藏在省略文字中。
- 大列表保留虚拟化；进度更新更新当前行，不重建整个列表或重播入场动画。
- 当前任务模板最小内容高度 86 DIP；新增紧凑行需保证文字与进度不会互相遮挡。

### 7.5 菜单、Tooltip、对话框与通知

- 菜单按动作关系分组；分隔线用于不同动作组，避免每项都分隔。
- Tooltip 只补充说明；关键状态、错误和操作入口直接出现在界面中。
- 对话框遵循“说明 → 输入 → 底部操作”；取消在确认之前，默认动作须明确。
- Escape 关闭可取消对话框；Enter 在适用输入上下文中执行默认动作，不抢占多行输入。当前各窗口需逐项验证 / 接入。
- 完成通知同时说明成功与失败数量，并提供结果入口。关闭通知不影响任务数据。
- 常规成功采用轻提示；影响操作的错误提供稳定详情与日志，不只出现短暂动画。
- 关闭窗口立即执行；没有为淡出延迟窗口关闭或任务取消。

### 7.6 进度、空状态与拖放

- 确定进度表达真实数值；状态文字即时更新。不能通过缓动生成尚未完成的进度。
- 无法估计进度时使用不确定指示与“正在读取…”；结束后及时停止。
- 空状态应解释下一步，如“添加文件，或选择左侧格式”；任务全被筛选掉时说明筛选条件。当前空队列仍是空白区域，待接入。
- 拖入有效文件时给出清晰接受提示；无效文件给出原因。拖放蒙层属于待完善组件。
- 移除、重排与清空都先更新真实数据；动效不能阻止下一次操作。

### 7.7 媒体预览、时间轴与裁剪

- 媒体画面按比例显示，不拉伸；预览黑底、等待 / 失败文字可读。
- 时间轴拖动、裁剪手柄与鼠标同步；不为拖动坐标加缓动或跟随延迟。
- 时间与坐标输入提供拖动操作的键盘替代；业务参数变化保留实际值。
- UI Motion 与输出媒体的 `FadeIn` / `FadeOut` 分开。减少界面动效不修改视频 / 音频淡入淡出参数。
- 裁剪框、选中区、起止手柄在亮 / 暗媒体画面上都应可识别；这是自绘控件需单独验证的内容。

## 8. Motion 系统

Avalonia 属性变化可使用 `Transitions`；多阶段变化可使用关键帧 `Animation`；视图切换也可使用 `IPageTransition`。项目当前使用前两种，以保持现有窗口结构。[Avalonia 11 Animations](https://v11.docs.avaloniaui.net/docs/basics/user-interface/animations/)、[Transitions](https://v11.docs.avaloniaui.net/docs/guides/graphics-and-animation/transitions/)

### 8.1 动效参数与场景

| Token / 场景 | 时长 | 缓动 | 实际变化 | 状态 |
| --- | --- | --- | --- | --- |
| `MotionFast`：按钮悬停 / 按压 | 120 ms | `CubicEaseOut` | 模板 presenter 的背景与边框颜色 | 已实现 |
| `MotionFast`：启用 / 禁用 | 120 ms | `CubicEaseOut` | Button 的 Opacity | 已实现 |
| `MotionFast`：磁贴按压 / 松开 | 120 ms | `CubicEaseOut` | `scale(1)` ↔ `scale(0.985)`；中心原点 | 已实现 |
| `MotionNavigate`：分类切换 | 160 ms | `CubicEaseOut` | 新网格内容 Opacity 从最多 0.82 到原值 | 已实现；非双视图交叉淡化 |
| `MotionEnter`：窗口内容入场 | 180 ms | `CubicEaseOut` | 内容 Opacity 从最多 0.82 到原值 | 已实现；原生窗口仍由系统管理 |
| 同层 Tab 内容切换 | 160 ms | `CubicEaseOut` | 新内容短淡化 | 后续接入 |
| 通知 / 新增任务行 | 180 ms 上限 | `CubicEaseOut` | 可用透明度与不超过 6 DIP 位移 | 后续组件按需接入 |
| 焦点、校验提示、时间轴、裁剪坐标 | 0 ms | — | 立即到目标状态 | 通用要求 |
| 关闭、取消、停止任务 | 0 ms 业务等待 | — | 立即执行动作 | 通用要求 |

时长集中定义在 `Styles/Motion.axaml`，业务窗口按资源名调用。`CubicEaseOut` 适用于小范围反馈 / 入场；连续等速旋转才使用 Linear；当前无装饰性无限动画。

### 8.2 触发、取消与最终状态

- 属性反馈使用 Transitions：从当前显示值向最新目标过渡，快速移动鼠标时不能排队补播。
- `Motion.Reveal(control)` 只对可见、已连接窗口且启用了 motion 的内容启动一次关键帧动画。
- 同一 Control 再次 Reveal 会取消上一轮。移出视觉树、控件自身隐藏、窗口隐藏 / 关闭、切换到减少动效会取消正在运行的淡入。
- `FillMode.None` 在完成 / 取消后恢复控件基础 Opacity；不永久写入 0 或保留动画值。
- 动画运行与设置在 UI 线程执行。业务命令先执行，不 `await` 装饰性动画。
- 每个视觉属性同一时刻只有一个动效负责人；不要在同一容器同时加 Reveal 淡入与 Opacity transition。
- 分类切换更新新内容后淡入；不保留旧网格或让旧内容参与命中测试。

### 8.3 减少动效与平台偏好

- 设置中提供“减少界面动效（立即显示状态变化）”，保存为 `AppSettings.ReduceMotion`，旧设置缺少该字段时默认为 false。
- 生效条件：应用开关关闭，且 Windows 未关闭客户端区域动画，才允许本项目 Motion。
- Windows 使用 `SPI_GETCLIENTAREAANIMATION`（`0x1042`）只读查询；系统设置变更会在窗口再次激活 / 打开或保存应用偏好时重读。[Microsoft SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- Avalonia 11.3.22 的 `IPlatformSettings` 未提供通用减少动效接口；其他系统当前使用应用开关，后续平台适配须独立实现。[11.3.22 接口源码](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Base/Platform/IPlatformSettings.cs)
- 减少动效时撤销 `Window.motion-enabled` 类，取消淡入；Hover、Pressed、Focus、Disabled 最终视觉仍存在，立即显示。
- 本开关控制 AvaMedia 自定义 UI Motion。原生窗口、基础主题内部指示器、媒体播放按各自机制运行；后续新增循环指示器必须接入同一偏好。
- 动效不能成为理解状态的唯一途径；禁止闪烁、大片背景平移、弹簧反复回弹与自动视差。[Fluent Accessible Motion](https://fluent2.microsoft.design/motion)、[WCAG 交互动效](https://www.w3.org/WAI/WCAG22/Understanding/animation-from-interactions.html)

### 8.4 性能边界

- 首选 Opacity / RenderTransform / 小范围颜色变化。不要为进度帧更新、鼠标拖动或每个任务行不断做动画。
- 不动画化 Width / Height / Margin / Padding 的高频变化，以避免反复布局。
- 不给整个功能网格逐项累计延迟；当前一次容器淡入，总耗时 160 ms。
- 离屏、隐藏、关闭内容停止业务侧动画；不假定所有 Transitions 都会自动暂停。
- 大队列和转换高负载下检查输入响应；60 fps 是视觉目标，实际帧率须在目标硬件测量。

## 9. Avalonia 样式实现规则

| 职责 | 文件 / 机制 |
| --- | --- |
| 基础控件模板 | `Avalonia.Themes.Simple` 11.3.22 |
| 公共 Token 与组件外观 | `Styles/UiStyles.axaml` |
| 公共 Motion 参数与状态过渡 | `Styles/Motion.axaml` |
| 动效偏好 / 窗口生命周期 / 可取消入场 | `Motion.cs` |
| 组合布局与数据 | 各窗口 XAML / C# |
| 稳定、可替换的组件模板 | 按需新增 `ControlTheme` |

`ControlTheme` 用于模板级组件，Style / Classes 用于外观与状态。模板需要命名部件时保留其契约；不应为改颜色复制整套控件模板。[Control Themes](https://v11.docs.avaloniaui.net/docs/basics/user-interface/styling/control-themes/)

```xml
<!-- 在 App.axaml 中：基础主题 → 通用样式 → 动效 -->
<SimpleTheme />
<StyleInclude Source="avares://AvaMedia.Desktop/Styles/UiStyles.axaml" />
<StyleInclude Source="avares://AvaMedia.Desktop/Styles/Motion.axaml" />

<!-- 新界面的语义表面与辅助文字 -->
<Border Background="{DynamicResource UiSurface}" Padding="16">
  <StackPanel Spacing="8">
    <TextBlock Classes="title" Text="输出设置" />
    <TextBlock Classes="caption" Text="选择格式与输出位置。" />
    <Button Content="添加文件…" />
  </StackPanel>
</Border>
```

```csharp
// 同步重建分类内容后调用；初次构造、不可见与减少动效时会直接跳过。
Motion.Reveal(FeatureGrid);

// 应用保存后的用户偏好，所有已打开窗口共同生效。
Motion.SetReducedMotion(settings.ReduceMotion);
```

- 有状态颜色的 `SimpleTheme` Button 实际修改 `PART_ContentPresenter`；因此颜色过渡挂在 presenter 上，按压变换挂在 Button 上。[11.3.22 Button 模板](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Themes.Simple/Controls/Button.xaml)
- `RenderTransform` 过渡使用 `scale(...)` 等 `TransformOperations` 表达式，并匹配 `TransformOperationsTransition`；不能假定普通 `ScaleTransform` 支持同样的 transition。
- StyleInclude 的局部 StaticResource 就近定义，避免依赖尚未挂到资源树的相邻 include；运行时主题色始终使用 DynamicResource。
- 避免局部 `Background="White"` 或 C# `Brushes.White` 覆盖主题。例外仅用于真实媒体表面、固定艺术资源和明确说明的蒙层。
- 动效由 `Window` 的 `Motion.IsEnabled` 附加属性统一注册；窗口自身不重复注册动画生命周期。
- 控件可以局部禁用 Motion 注册；所有新增动效仍必须遵循减少动效偏好，不能绕过全局设置。

## 10. 键盘、无障碍与交互一致性

- Tab 顺序遵循阅读 / 操作顺序，Shift+Tab 反向；所有重要操作通过键盘可达。
- Button / 输入 / Tab / ListBox 保留基础主题 focus adorner。Hover 动效不能延迟焦点出现。
- 无文字图标、裁剪与时间轴操作补充 AutomationProperties 名称 / 说明；可通过数值输入完成等价动作。
- 错误指出字段与修正办法；完成 / 失败状态同时有文字，允许复制详细日志。
- Tooltip 不承担必需信息；聚焦控件不能被浮层或底部操作遮挡。
- 任务进行中禁用会破坏运行的操作，保留停止 / 查看日志能力。
- 目标快捷键：Enter 确认、Escape 取消、Delete 移除选中任务、Ctrl+O 添加文件。业务窗口实际接入情况需逐项验证，不能只写在帮助中。
- 触屏模式、系统高对比度主题与屏幕阅读器播报属于专门验收项，当前未宣称已完整支持。

## 11. 文案与反馈约定

| 情况 | 建议文案 / 信息 |
| --- | --- |
| 等待 | “等待中”；显示目标格式 |
| 运行 | “转换中 42.0%”；无法估计时“正在处理…” |
| 完成 | “完成”；可打开文件 / 输出目录 |
| 失败 | “失败 · 查看日志”；保留输入与参数供重试 |
| 用户停止 | “已停止”；说明可重试，区别于失败 |
| 文件无效 | 写清文件与原因，保留已接受文件 |
| 未添加文件 | 指明添加入口，不只显示空表格 |
| 外部工具缺失 | 说明所需工具与设置入口 |

反馈首先反映真实任务状态。动画、图标与颜色辅助理解，不能更改状态名称或掩盖失败。

## 12. 验收清单与后续迁移

### 本次修改的检查

- [x] 使用本地 `.tools/dotnet/dotnet.exe` 构建解决方案：0 警告、0 错误。
- [x] 实际启动并捕获主窗口；样式、资源与附加属性加载成功。
- [x] 检查浅 / 深色主窗口、代表性组件与深色设置窗口；减少动效设置可见、无截断。
- [x] 分类快速切换、淡入中减少动效、控件移除 / 隐藏及窗口隐藏 / 关闭：取消后恢复基础透明度。
- [x] 按钮 Hover / Pressed / Disabled 与减少动效最终状态；键盘焦点即时出现，按压和焦点不改变布局边界。
- [x] 人工核对字体、间距、色值与动效参数表和资源代码。

验证入口：`tests/AvaMedia.UiChecks`，采用 Avalonia.Headless 11.3.22 加载实际应用样式、模拟按钮输入并运行 UI 消息循环。本次 **30 项检查通过**；报告见 `artifacts/ui-checks/results.json`，渲染结果见该目录的 `main-light.png`、`main-dark.png`、`components-*.png` 与 `settings-dark.png`。原生主窗口启动截图见 `artifacts/ui-after/main.png`。

当前 Windows 客户端区域动画偏好为关闭。测试先验证该策略，再仅在隔离的 headless 窗口添加 motion 类以验证动画；未修改系统开关。UI 检查不更改用户任务队列或偏好。高 DPI、多平台与屏幕阅读器完整验收尚未执行。

```powershell
& .\.tools\dotnet\dotnet.exe build AvaMedia.sln --no-restore
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.UiChecks
& .\.tools\dotnet\dotnet.exe run --project src/AvaMedia.Desktop --no-build -- --capture artifacts/ui-after
```

原生 `--capture` 会走主窗口正常关闭流程并保存当前设置 / 队列；只需验证样式与交互时使用隔离的 `UiChecks`。

### 后续界面开发必须覆盖

- [ ] 普通 / Hover / Pressed / Focus / Disabled / Busy / Selected / Error 状态。
- [ ] Light / Dark、最小窗口、长文件名、空列表、大队列。
- [ ] 100%–200% DPI、键盘、图标名称、文字与关键状态对比度。
- [ ] 减少动效、系统关闭动画、媒体转换高负载、连续点击与关闭生命周期。
- [ ] 新颜色 / 尺寸先补通用资源与本规范，再接入界面；例外写明原因与迁移状态。

优先迁移顺序：历史局部颜色 → 字号与间距 → 工具栏图标与空状态 → 内联校验 / 键盘默认动作 → 自绘编辑控件与其无障碍 → 按需扩展 Tab / 通知 Motion。

## 13. 维护规则

本文与 `UiStyles.axaml`、`Motion.axaml`、`Motion.cs` 一起维护。改变参数时同步更新 Token 表；新增通用组件时补充视觉、状态、键盘、主题与减少动效行为。将“设计目标”“已实现”“已验证”分别记录，验证通过后才更新验收清单。

本次调研使用 Avalonia 官方文档、锁定版本源码、Microsoft Fluent / Windows 文档及 W3C 规范解释。不要把其他版本文档中的 API 直接用于 11.3.22；具体代码以当前引用包编译与运行结果为准。

### 1.0.1 快速剪辑列表

新增 `QuickClipWindow.axaml` 遵循语义色资源与全局 Motion，列表水平滚动禁用以约束长路径，路径/文件名以省略号显示并提供 Tooltip。操作图标使用原创 `ActionIcon` 矢量，配可访问名称和 Tooltip；首尾移动按钮禁用。界面覆盖空列表、浅/深主题、逐文件参数、读取中/读取失败、确认读取等待与 Esc 取消；Delete 移除选中条目。

`tests/AvaMedia.QuickClipTests` 验证普通尺寸、820×480 最小尺寸、长文件名、横/竖屏缩略图、行操作不溢出、图标具有可见边界和减少动效。高 DPI、屏幕阅读器和超大队列完整验收仍保留为后续项。

### 视频批量裁剪：共享选区界面

`BatchCropWindow.axaml` 使用通用字体、语义色、4 px 圆角、12/16 px 间距和全局 Motion。源画面预览使用黑色背景，选区沿用现有 `CropOverlay` 的蓝色边框与白色角标；黑边不参与坐标映射。拖动期间坐标和选区即时更新，不对坐标或裁剪框增加动画，以保持指针与选区一致。

列表复选框表示“参与处理”，行选中表示“切换预览 / 待移除”，两者独立。始终显示勾选数量、参考视频、每个文件实际应用的选区和批次有效性。读取中、读取失败、越界和空列表都有文字状态；错误反馈绑定动态语义色，随浅 / 深主题变化。无效批次禁用默认确认按钮，取消支持 Esc；数字输入、模式、列表和时间滑块提供可访问名称，拖动选区同时提供数字输入替代。

窗口默认 1200×800，最小 940×640；文件名省略并提供完整路径 Tooltip，状态换行，水平滚动禁用。专项检查覆盖实际指针拖动、浅 / 深主题、反馈文字可见性、最小尺寸操作区与确认按钮边界，以及关闭时取消读取。高 DPI、屏幕阅读器和大批次压力测试尚未验收。

## 14. 编辑器组件统一规范（Light / Dark / Mac OS 9）

三套皮肤共享同一组组件角色与布局尺寸。皮肤改变配色、圆角与边框绘制；不得为同一角色另设字号、宽高或对齐规则。尺寸以 DIP 为单位，定义于 `UiStyles.axaml`，由最后加载的 `EditorStyles.axaml` 应用到 `Window.editor`。其他窗口复用这些角色时遵循相同指标；分类入口、功能磁贴与媒体画面保持各自的角色。

| 组件角色 / Token | 统一指标 | 使用位置 |
| --- | --- | --- |
| `UiFontBody` | 13 DIP | 标签、下拉框、文字按钮 |
| `UiNumericFont` / `UiTimeFontSize` | Consolas / Menlo / DejaVu Sans Mono；14 DIP | 所有编辑器时间读数与输入 |
| `UiFieldHeight` | 32 DIP | 时间输入、精度、速度、淡入 / 淡出与设置边界按钮 |
| `UiTimeFieldWidth` | 168 DIP | 开始 / 结束时间输入；文字居中 |
| `Button.step` | 32 × 32 DIP；16 DIP 矢量 | 时间减 / 加按钮 |
| `UiTransportSize` / `Button.transport` | 40 × 40 DIP；24 DIP 矢量 | 定位、播放 / 暂停、停止、静音 |
| `UiDialogActionWidth` / `UiDialogActionHeight` | 168 × 36 DIP | 取消 / 确定或下一步；两个按钮同尺寸 |
| 同组 / 分栏 / 底部间距 | 8 / 24 / 12 DIP | 起止时间组件、两个预览区、对话框操作 |

当前位置、媒体总时长、开始时间、结束时间、持续时间与输出时长一律显示 `HH:mm:ss.fff`。小时为累计小时，毫秒四舍五入并正确进位；未知或无效的只读时间显示 `—`。时间文字与所在行的标签、播放控件垂直居中。不得混用 `mm:ss`、三位与六位小数，或用字号放大强调个别时间。

显示精度与媒体参数精度分开：未修改的三位小数文本须保留原始帧时间；用户明确输入更多小数时按其输入更新参数，再将显示归一为三位小数。定位、区间拖动、帧步进与输出计算仍使用原始精度；读取、确认及进入下一步不得重新量化边界。

播放与时间微调使用原创 `ActionIcon` 矢量，保持一致笔画与语义颜色；播放 / 暂停及静音状态改变图形、Tooltip 和可访问名称，不替换成 Unicode 字符。起止时间标题、标签页与底部文字按钮不混入装饰字符。预览保持黑底等比显示；时间输入、下拉框与两个边界预览对应对齐。

默认 1400 × 980、最小 1000 × 730。编辑标签内容至少保留 300 DIP 高度，缩小时优先减少上方预览空间，保持输入、淡入 / 淡出、区间播放与底部操作完整可见。新增侧栏操作须检查所在 Tab 内的可见边界，不能仅以窗口边界判断是否被裁掉。

验证入口：`tests/AvaMedia.EditorStyleTests`，已加入 `scripts/Verify.ps1`。覆盖三套皮肤的默认 / 最小尺寸、时间与标签对齐、组件尺寸、矢量与可访问名称、实际播放 / 暂停 / 静音，以及精确边界提交与高精度手动输入。运行会生成六张实际渲染图与 `report.json`；包含区间播放的编辑器版本额外检查该按钮在 Tab 内完整可见。
