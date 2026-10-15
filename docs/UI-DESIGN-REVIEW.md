# 界面源码审查与文档整理

日期：2026-10-11。范围：工具入口、桌面窗口与子弹窗、共享控件、四套皮肤，以及原生首次配置与 WiFi 网页的布局源码。依据 [UISPEC.MD](../UISPEC.MD)、当前实现与 [AGENTS.md](../AGENTS.md)；本轮只进行源码审查、必要静态诊断与受影响项目编译。

## 文档分工与实际覆盖

| 文档 | 维护内容 |
| --- | --- |
| [架构与接口](ARCHITECTURE.md) | 模块依赖、工具与任务流转、共享呈现层、服务和资源所有权 |
| [界面规范](../UISPEC.MD) | 当前语义资源、四套皮肤指标、布局、控件、键盘、反馈和证据边界 |
| [工具界面](TOOL-INTERFACES.md) | `Catalog.All` 的全部入口与实际工作区、统一配置和结果流程 |
| [动态布局](UI-LAYOUT-STABILITY.md) | 状态、计数、时间与操作文案变化时的布局稳定规则 |
| 各功能文档、`docs/acceptance/` | 操作与限制、注明日期和范围的历史运行证据 |

当前目录有 **61 个工具入口**，按视频 17、音频 14、图片 12、文档 8、工具集 8、光盘 2 核对。实现清单包含 **39 个 Window 类、15 处内联 Window、38 个 AXAML 文件**。同一类的分部文件合并计数，共用格式工作区不重复计为窗口；AXAML 同时包含窗口、UserControl、应用与样式。

先沿 `Catalog.All → MainWindow.Configure / 任务编辑 → 工作区 / 子弹窗` 核对入口，再检查布局根、最小尺寸、固定列与伸缩列、滚动区、底部动作、字段语义、动态状态及共享资源。清单来自共享工作区当时的源码，包含正在开发的 AI 任务和方向结果界面；其他任务的未提交修改不归入本次提交。

## 发现与修正

| Before | After | Why |
| --- | --- | --- |
| 浅 / 深色按钮已标 `primary`，但没有对应强调样式 | 增加强调背景、前景及悬停、按压、键盘焦点状态；限定浅 / 深窗口 | 主要动作形成可辨认层级，Mac OS 9 / XP 继续使用自身默认按钮面 |
| 自定义“应用选择”“播放”“保存词库”等确认未明确默认键 | 由调用处显式设置 `isDefault`，统一 `dialog-action` | 安全确认支持 Enter；自定义危险确认不根据文字自动绑定 Enter |
| 完整显示的 TextBlock 仍重复弹出相同文字 | `OverflowTip` 在文字实际省略时才允许重复内容提示 | 减少悬停遮挡，保留额外完整路径和真实错误详情 |
| 输入名称只在 `Ui.Parameter` 的数值输入分支设置 | 文本、下拉框与数值均设置字段名称，数值滑条沿用名称 | 键盘与辅助技术能够识别输入用途，减少各窗口重复实现 |
| 下载监控的说明文字为 10 DIP，模型状态符号为 11 DIP；状态颜色固定 | 使用 `UiFontCaption`、语义状态色与强调色前景 | 达到当前至少 12 DIP 的文字规则，并随皮肤和主题变化 |
| 媒体信息最小宽度 700，固定列、间距和边距只给详情留下约 20 DIP | 文件列 200、详情与预览各用伸缩列；禁用文件列表横向滚动 | 最小宽度下详情与预览各保留约 210 DIP 的布局预算 |
| 看图批量导出的设置没有滚动、无效选择仍可开始，执行时可点全选 | 设置独立滚动；有效图片与路径启用开始；运行时锁定所有选择入口 | 缩小窗口仍可访问设置，操作状态与正在处理的快照一致 |
| 看图导出所有格式都显示质量；状态高度不预留 | JPEG / WebP / AVIF 显示质量，其他格式隐藏且不读取该输入；状态用 `Ui.Status` | 参数与编码用途一致，进度文字变化不推移固定底部操作 |
| 看图导出开始 / 取消缺少统一角色，运行取消文案不变 | 主动作与取消使用对话框角色；运行时显示“停止”，预留两种文案宽度 | 操作含义明确，按钮位置稳定 |
| 看图“最近打开”只有双击路径，没有固定确认 / 取消 | 长路径省略并可补全，选中后启用“打开文件”，保留双击和键盘默认键 | 鼠标和键盘都有完整可见的打开流程 |
| 看图效果弹窗没有取消与最小尺寸，应用随表单放在纵向 StackPanel | 增加最小尺寸、字段名称、可滚动表单和固定取消 / 应用 | 操作区保持可见，字段与提交有清晰层级 |
| 人声试听状态与按钮在同一纵向 StackPanel | 状态独立滚动，试听和关闭位于固定底部行 | 错误或长状态不会把关闭动作推到窗口外 |
| 分类分组弹窗有重复引导，错误使用普通 caption，词库复选框缺少项目名称 | 删除重复引导、使用错误角色、提供词库项目名称 | 突出真实内容与错误，明确辅助技术中的选择对象 |
| 架构缺少模块与生命周期图，工具清单和皮肤尺寸过时；旧通过清单没有清晰版本范围 | 重建模块与任务图、62 入口映射、四皮肤尺寸表和审查索引，历史证据标明范围 | 文档能够指导当前实现，避免将旧测试或构建结果当成当前全部界面验收 |

主按钮沿用当前 Avalonia 11.3.22 [SimpleTheme 按钮模板](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Themes.Simple/Controls/Button.xaml) 的命名部件和状态，不替换点击、键盘或禁用行为。主按钮与模型状态圆点的资源色对做了静态对比度计算：浅色主按钮正常态为 4.53:1，深色为 9.06:1；本次所用悬停、按压及状态符号色对均高于 4.5:1。这是指定实色资源的计算，不能代替所有界面、经典皮肤或媒体画面的视觉与无障碍验收。

## 桌面窗口覆盖

以下每行列出的类均检查布局结构和对应分部文件的入口 / 动态状态；“核对”是源码结论，不代表运行交互通过。

| 窗口类 | 检查重点与本轮处理 |
| --- | --- |
| `MainWindow` | 用途分组、62 工具配置入口、队列、任务行、通知、菜单、Beta 门控与文件路由；复用共享 primary / 状态 / Tooltip 规则 |
| `SettingsWindow`、`OptionsWindow` | 分类导航、长设置页滚动、模型 / 供应商状态、应用 / 取消；补普通选项字段名称 |
| `MediaRouteWindow` | 文件到工具映射、可见候选、别名去重与返回既有配置入口 |
| `ConvertWindow` | 格式与来源差异、排序、参数、输出及固定确认区 |
| `EditorWindow`、`QuickClipWorkspaceWindow`、`ClipSplitWindow`、`ClipExportWindow` | 预览、时间轴、片段、数值替代操作与导出页；修正文档尺寸与实际主入口；无生产入口的旧 `QuickClipWindow` 已移除 |
| `VideoCompressionWindow`、`VideoSlimmingWindow` | 模式相关字段、体积结果、预览、输出与状态预留 |
| `BatchCropWindow`、`BatchRotateWindow` | 列表勾选 / 行选择、前后预览、数字参数、批次有效性、默认确认与取消 |
| `ImageCompressionWindow` | 文件、前后对比、格式相关参数、体积与输出 |
| `ImageViewerWindow`、`ImageViewerExportWindow` | 菜单、近期文件、效果、批量选择、设置滚动、质量适用性、开始 / 停止及状态；已修正 |
| `PdfWorkspaceWindow` | 工具相关排页 / 预览、设置、输出、空列表与状态 |
| `DownloadWindow`、`BrowserDownloadWindow` | 链接、解析结果勾选、网页视口、下载进度、固定动作与错误 |
| `SpeechToolsWindow`、`SubtitleReviewWindow`、`VoicePreviewWindow` | 识别 / 增强配置、时间定位、字幕校对与样式、试听；修正试听长状态布局 |
| `MediaAiWindow`、`WordLibraryWindow`、`WordLibraryEditorWindow` | 结果证据、模型状态、标签编辑、词库选择和保存；补默认动作及词库选择名称 |
| `FolderClassificationWindow` | 分类筐 / 文件 / 预览、分组 / 规则、服装参数、状态与部分结果；修正分组文案、错误和确认 |
| `PersonClipWindow` | 分析状态、人工调整片段、预览、参数与输出 |
| `VideoSummaryWindow`、`VideoSummaryResultWindow` | 模型与总结配置、阅读 / 编辑、字幕 / 画面 / 证据、可滚动结果与动作 |
| `MultiStageTaskWindow` | AI 任务（有人片段、自动旋转、字幕、视频总结、标签/整理）的阶段列表、预览、结果、暂停 / 结束；旧 `AiTaskWindow` 已删除 |
| `RenameWindow`、`ContactSheetWindow` | 文件计划与选择、命名规则 / 词库、截图参数与预览、输出 |
| `PlayerWindow`、`DiscOpenWindow` | 菜单、播放控制、时间、轨道 / 原盘选择、播放列表；补原盘字段名称与默认播放 |
| `MediaInfoWindow` | 文件、详情、预览、导出与最小宽度；修正伸缩列和状态预留 |
| `WifiTransferWindow` | 地址 / 二维码、目标目录、传输列表、状态与服务开关 |
| `HelpWindow`、`HardwareTestWindow` | 阅读 / 日志滚动、真实检测状态、取消与关闭 |

## 15 处内联子弹窗覆盖

| 构造位置 | 弹窗与检查内容 |
| --- | --- |
| `FolderClassificationWindow.Outfits / Presets / Rules`（3） | 相似服装阈值、分组选择、分类规则；字段、错误、滚动与确认 |
| `ImageViewerWindow.Actions / Navigation`（2） | 预览效果与最近打开；已补齐固定动作与最小尺寸 |
| `MediaAiWindow.CaptionSettings / Edits / Layout / Rename`（5） | 画面分析、标签编辑、单标签资料、高级设置、标签重命名；长正文、字段与动作 |
| `OptionsWindow`（1） | 保存预设；名称输入与保存 / 取消 |
| `PersonClipWindow.Analysis`（1） | 高级设置；长表单与底部操作 |
| `PlayerWindow`（1） | 移入回收站确认；明确危险动作和取消，不默认绑定危险确认 |
| `SubtitleReviewWindow`（1） | 字幕样式；预览、表单、应用与取消 |
| `Ui.Confirm`（1） | 共享危险确认；单一主要动作与取消，保留确认上下文 |

## 共享组件、皮肤及附属界面

- 38 个 AXAML 文件核对 XML 结构、根布局、列宽、滚动、角色与动态资源；C# 窗口核对相同约束。窗口、任务与设置的重复状态继续由 `StableLayout` 和 `Ui.Status` 预留。
- `Ui`、`ToolExecution`、字段 / 时间 / 预览 / 默认按钮角色和 `Localization` 核对可访问名称、资源键与用户数据保护。新增文案复用现有语言键。
- 预览、裁剪、时间轴、波形、标签图表、负载数字和下载曲线核对字体、坐标 / 数字替代、真实状态与请求生命周期；本轮没有验证自绘控件的实际像素或指针行为。
- Light / Dark 的字段 32 DIP、对话框动作 168×36 DIP；Mac OS 9 为 22 DIP、92×26 DIP；Windows XP 为 24 DIP、88×26 DIP。完整字体、菜单、时间与队列指标已同步到 `UISPEC.MD` 第 15 节。经典窗口装饰、资源加载及 Motion 的减少动效入口按源码核对。
- 原生首次配置检查 `AvaMedia.Bootstrap/SetupWindows.h` 与 `Mac.m` 的平台控件、字段、分组和底部动作；WiFi 检查 `WifiTransfer/index.html` 的表单、上传状态与响应式结构。两者不套用 Avalonia 模板，本轮未修改、编译或运行这些附属界面。

## 本地检查与边界

受影响 Desktop 项目已完成 Release 编译，连同 Core / MCP 项目引用，**0 警告、0 错误**：

```sh
/Users/mcx/.dotnet/dotnet build src/AvaMedia.Desktop/AvaMedia.Desktop.csproj -c Release --no-restore
```

另检查本次修改的补丁空白、AXAML XML 结构与文档本地链接。没有新增测试，没有执行功能回归、消融、截图 / 像素检查、媒体输出、性能或压力测试，也没有等待后台 CI。

本轮可以确认文档和源码布局 / 角色约束已对齐、所修改项目可编译；Windows / macOS 实际交互、DPI、英文长文案、四套皮肤观感、屏幕阅读器与自绘媒体控件仍未进行本轮运行验收。历史证据继续按各自日期、提交与环境解释。
