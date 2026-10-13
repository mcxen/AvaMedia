# 顶层窗口外观与独立窗口检查（2026-10-13）

代码提交：`ab1efd8`（统一窗口框架）、`c777807`（工具窗口独立切换）。本次在 main 开发，只提交本任务的路径或修改块，未推送、未打 tag。已有 winchrome 草稿保留并修正；共享工作区的字体、尺寸、控件审计和 pickerv2 改动未纳入本任务提交。

## 根因与边界

1. 主窗口 `MainWindow.WindowChrome.cs`、编辑器 `EditorWindow.Chrome.cs`、`Controls/WindowChrome.cs` 各自配置窗口装饰。Linux 分支会设 `SystemDecorations.None` 并添加透明缩放手柄；只有显式 WrapDialog 的部分工具走这条路径，其他工具及临时 Window 使用默认系统框架。经典皮肤的 Skin 注册也会禁用系统装饰，安装预览还单独去掉装饰。因而整个窗体的边框、圆角和系统阴影资格不一致。
2. 业务 Window 默认从 `UiStyles.axaml` 获得不透明 `UiCanvas`，主窗口另用不透明 `ShellWindow`；逐个检查未发现原有业务 Window 通过透明 Background 自绘整个圆角窗体。不能将“透明整窗”写成已经证实的现有根因。新统一入口显式禁止逐像素透明宿主，避免后续圆角 Border 再次剥夺原生形状和阴影。
3. Avalonia 11.3.22 的 Win32 后端在扩展客户区时已有 DWM 圆角调用，但 `UpdateExtendMargins` 在 WinUI composition 路径选择 0px DWM 边距，窗口尺寸 / 状态变更会重设边距。Windows 缺阴影与该路径相符；Windows 本次未运行，不能将源码推断写成已在真机确认的唯一根因。整窗圆角还取决于原生框架、DWM、窗口状态、驱动和会话环境。
4. `Show(owner)` 创建原生所属关系；设置、词库、硬件测试、试听、专属重命名和导出工具还使用了 ShowDialog。该关系会使工具依附主窗，影响最小化、任务栏 / Dock 切换。重启前草稿还将媒体窗口改为 Show(owner)，本轮按用户的新要求撤除。Owner 不负责提供阴影，也不应用它制造层级感。
5. Win10 没有 Win11 的原生窗口圆角偏好 API（最低 Windows build 22000）。因此 Win10 保留系统直角与阴影；Win11 请求原生圆角。透明背景、SetWindowRgn、自绘外阴影均不用于伪造 Win10 的 Win11 外观。

依据：[Microsoft 原生圆角要求](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)、[圆角偏好的最低系统版本](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_window_corner_preference)、[Avalonia 11.3.22 Win32 实现](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Windows/Avalonia.Win32/WindowImpl.cs)。

## 改法

- `Skin.Registration` 注册所有 Window（包括临时 new Window），将未包装的工具客户区接入共享 WrapDialog。主窗口、编辑器保留业务布局，调用 `WindowChrome.ApplyPlatformFrame`。统一 `SystemDecorations.Full`、不透明主题 Background 和 `TransparencyLevelHint=[None]`。
- Windows 现代窗口使用客户区扩展 + NoChrome，保留原生系统框架；DWM 设 1px 边缘维护原生阴影。打开、状态变化和 WM_SIZE 完成后的调度回调重新应用。Win11 正常窗口调用 `DwmSetWindowAttribute(hwnd, 33, 2, sizeof(int))`，即 `DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND`；最大化 / 全屏用系统默认，贴靠形状由 DWM 决定。Win10 不调用 Win11 圆角 API。
- macOS 现代窗口 Full + `PreferSystemChrome | OSXThickTitleBar`，NSWindow 提供圆角、阴影和系统按钮。Linux 保留原生 WM 装饰、缩放和合成器能力，不再按窗口单独取消装饰。经典皮肤绘制客户区框架，外层也保留原生宿主；Windows 经典标题扩展高度为 0，macOS / Linux 保留系统标题栏。
- 非模态工具 `ShowIndependent` 设置 `ShowInTaskbar=true`，将 CenterOwner 改为 CenterScreen，并调用无 Owner 的 Show。媒体窗口可根据来源窗口的屏幕定位，仍不设置 Owner。需要等到关闭的工具使用 Closed 完成 Task；ToolExecution 的提交结果路径继续保留。确实需要返回用户确认或编辑结果的模态表单保留 ShowDialog。
- 编辑器方向检测、分类提交高级编辑、设置中的专属重命名通过应用的 MainWindow 取得业务上下文，不再依赖 Owner 链。窗口层级感仅依靠系统阴影，不使用 Topmost。
- 更新 UISPEC.MD 第 8 节及 docs/WINDOW-CHROME.md。共享标题按钮遵守 CanMinimize / CanResize / CanMaximize，去掉已不使用的 X11 自绘缩放边。

## 逐个 Window 子类检查

以下 42 个 Window 子类均由全局 Window 样式启用 Skin 注册，最终都应用上述 Full / 不透明 / None 平台策略；内部卡片 CornerRadius、BoxShadow 和透明控件不改变外层窗体。表中“模态”仅指需要返回表单结果的调用，不包括独立工具。AXAML 中原有 CenterOwner 的非模态窗口由 ShowIndependent 在显示前改为 CenterScreen。

| Window 子类 | 源码（Desktop 内） | 标题接入 | 打开方式 |
| --- | --- | --- | --- |
| `AiTaskWindow` | `AiTaskWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `BatchCropWindow` | `BatchCropWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `BatchRotateWindow` | `BatchRotateWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `BrowserDownloadWindow` | `BrowserDownloadWindow.axaml.cs` | Skin 自动包装 | 模态：选择下载条目并返回结果 |
| `ClassificationRunWindow` | `Classification/ClassificationRunWindow.cs` | 显式 WrapDialog | 独立 Show / ShowIndependentAsync |
| `ClassificationSubmitWindow` | `Classification/ClassificationSubmitWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `ClipExportWindow` | `ClipExportWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `ClipSplitWindow` | `ClipSplitWindow.cs` | Skin 自动包装 | 模态：返回拆分结果 |
| `ContactSheetWindow` | `ContactSheetWindow.cs` | 显式 WrapDialog | 独立 Show / ShowIndependentAsync |
| `ConvertWindow` | `ConvertWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `DownloadWindow` | `DownloadWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `EditorWindow` | `EditorWindow.axaml.cs` | EditorWindow.Chrome → 共享平台策略 | 快速剪辑独立；返回编辑结果的子表单模态 |
| `ExclusiveRenamerWindow` | `ExclusiveRenamerWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `FolderClassificationWindow` | `FolderClassificationWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `HardwareTestWindow` | `HardwareTestWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `HelpWindow` | `HelpWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `ImageCompressionWindow` | `ImageCompressionWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `ImageViewerExportWindow` | `ImageViewerExportWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `CoverPreviewWindow` | `ImageViewerWindow.Preview.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `ImageViewerWindow` | `ImageViewerWindow.cs` | MediaViewerWindow → WrapDialog | 独立 Show / ShowIndependentAsync |
| `MainWindow` | `MainWindow.axaml.cs` | MainWindow.WindowChrome → 共享平台策略 | 应用主窗口；用户主动托盘隐藏及离屏安装预览除外 |
| `MediaAiWindow` | `MediaAiWindow.cs` | Skin 自动包装 | 独立工具 / 独立进程主窗口 |
| `MediaInfoWindow` | `MediaInfoWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `MediaRouteWindow` | `MediaRouteWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `OptionsWindow` | `OptionsWindow.cs` | Skin 自动包装 | 模态：返回转换选项 |
| `PdfWorkspaceWindow` | `PdfWorkspaceWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `PersonClipWindow` | `PersonClipWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `DiscOpenWindow` | `Player/DiscOpenWindow.cs` | Skin 自动包装 | 模态：返回光盘源 |
| `PlayerWindow` | `PlayerWindow.axaml.cs` | MediaViewerWindow → WrapDialog | 独立 Show / ShowIndependentAsync |
| `QuickClipWorkspaceWindow` | `QuickClipWorkspaceWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `RenameWindow` | `RenameWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `SettingsWindow` | `SettingsWindow.axaml.cs` | 显式 WrapDialog | 独立 Show / ShowIndependentAsync |
| `SpeechToolsWindow` | `SpeechToolsWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `SubtitleReviewWindow` | `SubtitleReviewWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `VideoCompressionWindow` | `VideoCompressionWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `VideoSlimmingWindow` | `VideoSlimmingWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `VideoSummaryResultWindow` | `VideoSummaryResultWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `VideoSummaryWindow` | `VideoSummaryWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `VoicePreviewWindow` | `VoicePreviewWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `WifiTransferWindow` | `WifiTransferWindow.axaml.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |
| `WordLibraryEditorWindow` | `WordLibraryEditorWindow.cs` | Skin 自动包装 | 模态：返回编辑结果 |
| `WordLibraryWindow` | `WordLibraryWindow.cs` | Skin 自动包装 | 独立 Show / ShowIndependentAsync |

临时 `new Window` 共 17 处，同样经 Skin 注册与共享包装；原生 StorageProvider 对话框交给操作系统，不属于业务 Window 子类。

| 临时窗口位置（Desktop 内） | 标题 | 打开方式 |
| --- | --- | --- |
| `FolderClassificationWindow.Outfits.cs:20` | `"相似服装"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `FolderClassificationWindow.Presets.cs:59` | `"选择分组"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `FolderClassificationWindow.Rules.cs:131` | `"分类设置"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `ImageViewerWindow.Actions.cs:139` | `"预览效果"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `ImageViewerWindow.Navigation.cs:301` | `"最近打开"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `MediaAiWindow.CaptionSettings.cs:18` | `"画面分析与总结"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `MediaAiWindow.Edits.cs:37` | `"编辑标签"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `MediaAiWindow.Edits.cs:70` | `tag.Label` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `MediaAiWindow.Layout.cs:191` | `"AI 标签 · 高级设置"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `MediaAiWindow.Rename.cs:31` | `"标签重命名"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `OptionsWindow.cs:92` | `"保存预设"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `PersonClipWindow.Analysis.cs:27` | `"人物剪辑 · 高级设置"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `PlayerWindow.axaml.cs:409` | `"移入回收站"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `ToolFilePicker.cs:94` | `Localization.Text(options.Title)` | 独立 Show；仅此打开调用改动，选择器内容留给 pickerv2 |
| `Ui.About.cs:18` | `"关于 AvaMedia"` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `Ui.Components.cs:78` | `title` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |
| `Ui.Components.cs:95` | `title` | 确认 / 编辑 / 选择结果表单，保留模态；关于页无调用方时为独立窗口 |

`MainWindow.Background` 的用户主动托盘隐藏和 `CreateSetupPreview` 的离屏预览继续允许 ShowInTaskbar=false；它们不是新工具窗口。关于页没有调用窗口时直接 Show，默认 ShowInTaskbar=true 并由 Skin 接入同一外观。媒体源窗口参数只用于屏幕定位。文件选择器仍可在调用窗口关闭时结束选择，但主窗口最小化不会隐藏它，也没有原生 Owner。

## 构建和深色截图

仅构建 Desktop 项目及其直接项目引用，命令：

```sh
~/.dotnet/dotnet build src/AvaMedia.Desktop/AvaMedia.Desktop.csproj -p:EnableWindowsTargeting=true
```

首次共享工作区构建通过，0 警告、0 错误。后续共享构建分别遇到并行任务的 PlatformAppearance.cs / FileBrowserView.Compact.cs 编译错误，没有修改别人的源码。最终从本任务暂存内容导出 `/tmp/avamedia-winchrome-source`（源树与代码提交 c777807 完全一致），用上述命令构建 Desktop 通过，0 警告、0 错误，耗时 11.93 秒；构建日志在截图目录 build.log。这是本任务提交源码的编译验收，不代表并行未提交改动通过验收。未运行功能回归、媒体输出验证、性能、压力或全量测试。

按用户要求以最终构建产物启动：

```sh
DISPLAY=:11 AVAMEDIA_CHROME=mac XDG_DATA_HOME=/tmp/avademo/data \
  ~/.dotnet/dotnet /tmp/avamedia-winchrome-source/src/AvaMedia.Desktop/bin/Debug/net8.0/AvaMedia.Desktop.dll --dark
```

截图目录：`/workspace/AvaMedia-sidebar-review/winchrome/`。使用 X11 实际显示截图，包括窗口管理器的整窗框架；不用 RenderTargetBitmap。capture.py 先激活目标窗口，再按 X11 原生框架边界截取；工具图部分保留 16px 外围以观察叠放关系。只截深色应用皮肤。Linux 原生 WM 标题栏颜色由桌面环境决定，客户区占位红绿灯只预览 mac 布局。

| 文件 | 检查内容 |
| --- | --- |
| main-dark.png | 主窗口整窗装饰、外形、标题框架 |
| settings-dark.png | 自带 WrapDialog 的设置窗口，保留系统边框 |
| convert-dark.png | 原本默认装饰的转换工具，自动共享窗口包装 |
| picker-dark.png | 临时 new Window 文件选择器，原生框架和独立打开 |
| player-dark.png | MediaViewerWindow 来源屏幕定位、独立播放器 |
| wifi-dark.png | WiFi 非模态工具的同一外层系统框架 |
| editor-dark.png | 编辑器保留布局并调用统一平台策略 |
| help-dark.png | 帮助工具的共享框架 |
| settings-main-minimized-dark.png | 主窗口最小化后设置仍可显示、激活 |

同名 JSON 保存 WM_TRANSIENT_FOR、窗口类型、原生框架边距和任务栏状态。`main-minimized.json` 记录主窗口为 Iconic / HIDDEN，而格式转换、设置、播放器、WiFi、编辑器均为 Normal；五个工具均没有 WM_TRANSIENT_FOR 和 SKIP_TASKBAR，类型为 NORMAL。这验证了本轮 Linux 原生窗口独立性，Windows / macOS 仍需实机核验。

当前 Xfwm/Picom 会话截图未呈现明显外部系统阴影；Picom 启动参数未启用 --shadow，未找到用户阴影配置。保留了 WM 原生装饰，但本轮不能声称已验收 Linux 外投影，更不能声称已验收 Windows DWM 或 macOS 阴影。未修改共享桌面合成器设置。截图期间编辑器预览显示 FFmpeg 不支持 scale 的 reset_sar 参数；该现有媒体环境问题不影响整窗框架检查，本次未修改媒体处理、未检验媒体输出。现有数据目录的 MCP 端口已被其它进程占用，未修改它们。

## Windows / macOS 真机待验收

Windows 无法在本 Linux 环境实测。以下均为待验收项，不是本轮通过项：

1. Win11 本地桌面普通状态：主窗、编辑器、设置、转换、下载、播放器、看图和分类工具都有 DWM 原生圆角与阴影；新开窗口与其下方窗口的层级感来自系统阴影。确认 HWND 保留原生框架样式，圆角偏好为 ROUND，DWM 调用成功；不存在透明 / region 整窗或额外影子窗口。
2. Win10：同一窗口集保留原生直角及阴影，任务栏独立切换；不要求 Win11 原生圆角。用户的系统“在窗口下显示阴影”设置、驱动及远程 / 虚拟会话可能影响结果。
3. 最大化、还原、贴靠、全屏切换、拖动缩放和跨显示器 DPI 变化后，原生形状和阴影正确恢复，没有 1px 黑边 / 透明边、拉伸伪影或重复标题栏。贴靠和最大化的系统直角符合平台行为。
4. 主窗最小化 / 托盘隐藏后，所有独立工具仍可见；任务栏、Alt+Tab 均可单独切换。关闭工具不连带关闭其它工具。真正模态的确认 / 选项编辑表单仍返回结果。
5. macOS 需单独真机验收 NSWindow 原生圆角、阴影、红绿灯、Dock 窗口列表及主窗最小化后的工具可见性；同应用 Dock 图标是否合并遵从系统行为。Linux AVAMEDIA_CHROME=mac 截图不能替代这些验收。
