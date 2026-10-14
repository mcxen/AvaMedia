# 桌面窗口框架

所有皮肤的整窗外观由 `Controls/WindowChrome.Platform.cs` 统一决定。`Skin.Registration` 为所有 `Window` 注册外观，在内容变更、主题切换、打开和窗口状态变化后应用；未接入标题布局的工具窗口及临时 `new Window` 自动使用 `WindowChrome.WrapDialog`。主窗口和编辑器保留各自布局，调用同一平台策略，禁止另行修改整窗装饰。

| 平台 | 整窗策略 | 标题布局 |
|---|---|---|
| Windows 10 / 11 | `SystemDecorations.Full`，不透明主题背景，`TransparencyLevelHint=[None]`。现代模式扩展客户区，`NoChrome`；保留系统框架样式、缩放、贴靠和 DWM 阴影。在打开、状态恢复及尺寸变化的 Win32 消息完成后设置 1px DWM 客户区边缘。Win11（build >= 22000）正常窗口请求 `DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND`；其他窗口状态用 `DWMWCP_DEFAULT`，形状交给系统。Win10 原生直角和阴影，无原生圆角 API。 | 标题行右侧共享最小化 / 最大化 / 关闭按钮。不能用 `SystemDecorations.None` 删除系统框架。 |
| macOS | 保留 `SystemDecorations.Full`、不透明窗口；扩展客户区，`PreferSystemChrome | OSXThickTitleBar`；原生 NSWindow 提供圆角、阴影和红绿灯。 | 主窗口标题行 52pt，红绿灯与右侧按钮同一垂直中心，侧栏卡片左侧内缩 20pt（窗口左边距 28pt）。工具树在滚动区外保留标题带。编辑器 44pt、工具窗口 48pt，红绿灯左边距 16pt。 |
| Linux | 所有窗口保留 `SystemDecorations.Full`，不扩展客户区，不去掉 WM 装饰。窗口管理器提供边框及缩放，合成器决定阴影和圆角；能力不足时允许一致的原生直角。 | 原生标题栏；工具窗口隐藏重复的客户区标题，业务标题内容仍可作为工具栏显示。 |

macOS 红绿灯统一由 `Controls/WindowChrome.Mac.cs` 从实际标题行计算位置，使用 [NSWindow.standardWindowButton](https://developer.apple.com/documentation/appkit/nswindow/standardwindowbutton(_:)) 获取系统按钮，保持原生视图层级、按钮尺寸及间距。布局完成后在 UI 线程对齐，响应打开、激活、标题、尺寸、缩放及状态变化；全屏 / 最小化 / 经典皮肤跳过。按钮宿主范围不足时扩展原生标题宿主，保证绘制与点击区域一致。坐标均为逻辑点，不乘 Retina 倍率。主窗口预览占位沿用同一标题行中心；真实 macOS 不显示占位。依据 [Apple 窗口规范](https://developer.apple.com/design/human-interface-guidelines/windows) 使用系统窗口控件，不仿绘红绿灯。

窗口背景必须使用不透明的主题资源。内部卡片的 `CornerRadius` / `BoxShadow` 不代表整窗形状；不得将整窗设成透明，再用自绘圆角 Border 或额外阴影窗口替代原生形状。Windows 的逐像素透明或窗口 region 会失去 DWM 圆角资格。[Microsoft 圆角说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)说明了原生框架要求及最大化、贴靠和虚拟机中的限制。

Avalonia 11.3.22 的 [Win32 后端](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Windows/Avalonia.Win32/WindowImpl.cs)已经在扩展客户区时请求 DWM 圆角；不能把问题简单归结为框架没有圆角调用。它的 WinUI composition 路径可使用 0px DWM 边缘，应用统一入口显式保留 1px 边缘，并在 Avalonia 完成窗口设置后重申圆角请求。Windows / macOS 真实外观仍须在对应系统验收。

Mac OS 9 / Windows XP 皮肤通过 `Skin.Registration` 的 `PlatinumWindowFrame` / `WindowsXPWindowFrame` 绘制客户区经典框架，整窗仍使用统一原生宿主。Windows 与 macOS 扩展客户区 + `NoChrome`（标题高度提示 0），保留 `SystemDecorations.Full` 与原生阴影，只显示一套经典标题控件；Linux 保留 WM 标题栏。设置和工具中的重复纯标题行隐藏，文件选择器等功能导航行保留，编辑器只保留模式工具栏。Avalonia 的 [macOS 原生实现](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/native/Avalonia.Native/src/OSX/WindowImpl.mm)在扩展客户区时根据 ChromeHints 隐藏系统标题栏，同时保持 NSWindow 的阴影。不得用 `SystemDecorations.None` 禁用外层系统阴影。

Platinum 字体及组件度量集中在 `Styles/PlatinumTokens.axaml`：12 DIP 像素文字与 24 DIP 计数、经典字段和直角表面。显式引用 Ac 主题的组合控件同样映射到 Platinum。`Skin.PixelText` 为弹出菜单、提示和下拉项保留原渲染模式并临时使用 Alias，换回其他皮肤时还原；设置与文件选择器使用对应字号令牌，不以局部固定字号缩放像素字。

所有非模态工具（包括设置、帮助、词库、硬件测试、试听、结果窗口、播放器、看图和封面预览）使用 `WindowChrome.ShowIndependent(window)`：`ShowInTaskbar=true`，`CenterOwner` 改为 `CenterScreen`，无 Owner 的 `Show()`。媒体窗口可根据来源窗口的屏幕显式定位，但不设置所属关系。主窗口最小化或隐藏时，工具继续显示；用户可通过任务栏 / Dock 的窗口列表独立切换。同一应用在 Dock 是否合并为一个图标由系统决定。不要为层级感设置 Owner 或 Topmost；层级感由系统原生阴影体现。

普通工具优先恢复仍在有效屏幕内的已保存位置；首次打开优先主窗口所在屏幕的右侧、其次左侧，放不下时在该屏幕工作区居中并裁限。不得回退到屏幕左上角或自动投放到其他屏幕。未显式设置 `WindowStartupLocation` 的独立窗口同样接入该策略；显式 Manual 位置及模态表单保留调用方策略。

定位使用 `DesktopScaling` 计算来源窗口的桌面边界，目标尺寸与间距按目标 `Screen.Scaling` 换算，不使用 `RenderScaling`。Avalonia 11.3.22 的 macOS 后端以逻辑点表示屏幕坐标，桌面缩放为 1，而 Retina 绘制倍率通常为 2；混用会将主窗口占地翻倍，错误地判断没有相邻空间。依据 [Window 的定位实现](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Controls/Window.cs)、[TopLevelImpl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Native/TopLevelImpl.cs) 与 [Screens.mm](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/native/Avalonia.Native/src/OSX/Screens.mm)。

若调用方需要等到工具关闭（例如释放设置服务或重新载入词库），使用 `ShowIndependentAsync`，通过 `Closed` 完成 Task，仍不禁用来源窗口。提交任务的非模态工具继续由 `ToolExecution.ShowAsync` 返回结果，它内部也使用独立 `Show()`。只有确实需要用户返回确认 / 编辑结果的提示、选项、编辑表单保留 `ShowDialog(owner)`；外观策略相同。业务队列、回调等上下文通过参数或应用的 `MainWindow` 获得，不通过工具窗口的 Owner 链查找。

标题行和队列栏留白支持 `BeginMoveDrag`，双击切换最大化；按钮、文本框、菜单、滑条、下拉及分隔条保留自身操作。固定大小 / 禁止最小化或最大化的工具窗口，其共享标题按钮也必须遵守对应能力。

主窗口、编辑器和工具窗口通过 `WindowChrome.HookDrag` 在窗口的 `PointerPressed` 隧道路由处理标题输入，不依赖透明标题控件成为事件源。顶部标题带从客户区顶端延伸到实际标题行底部，覆盖页面上边距和红绿灯两侧留白；主窗口队列栏按实际行范围包含外边距，不扩展到任务列表或侧栏正文。视觉树命中排除交互控件及其模板子元素，禁用控件也保留原点击区域；全屏不启动拖动。标题拆卸时移除窗口监听，换皮或替换内容不会留下重复监听。

拖动在左键按下事件中同步调用 Avalonia 的原生 `BeginMoveDrag`，先释放鼠标按下产生的隐式捕获，避免系统拖动循环接管释放事件后仍锁定原控件。依据当前依赖 Avalonia 11.3.22 的 [MouseDevice](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Base/Input/MouseDevice.cs)、[Window](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Controls/Window.cs) 和 [macOS WindowBaseImpl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/native/Avalonia.Native/src/OSX/WindowBaseImpl.mm)；macOS 的实际移动交给 `NSWindow.performWindowDragWithEvent`。

`AVAMEDIA_CHROME=mac|win|system` 控制布局选择；`mac` 在非 macOS 绘制占位红绿灯，只用于预览间距，不能改变真实平台的系统框架策略。Linux 的 mac 预览仍保留 WM 装饰；`system` 保留系统标题栏。`MACOS` 编译常量仅选择布局，平台 API 仍按实际操作系统调用。
