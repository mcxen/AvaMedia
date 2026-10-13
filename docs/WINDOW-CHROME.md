# 桌面窗口框架

所有皮肤的整窗外观由 `Controls/WindowChrome.Platform.cs` 统一决定。`Skin.Registration` 为所有 `Window` 注册外观，在内容变更、主题切换、打开和窗口状态变化后应用；未接入标题布局的工具窗口及临时 `new Window` 自动使用 `WindowChrome.WrapDialog`。主窗口和编辑器保留各自布局，调用同一平台策略，禁止另行修改整窗装饰。

| 平台 | 整窗策略 | 标题布局 |
|---|---|---|
| Windows 10 / 11 | `SystemDecorations.Full`，不透明主题背景，`TransparencyLevelHint=[None]`。现代模式扩展客户区，`NoChrome`；保留系统框架样式、缩放、贴靠和 DWM 阴影。在打开、状态恢复及尺寸变化的 Win32 消息完成后设置 1px DWM 客户区边缘。Win11（build >= 22000）正常窗口请求 `DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND`；其他窗口状态用 `DWMWCP_DEFAULT`，形状交给系统。Win10 原生直角和阴影，无原生圆角 API。 | 标题行右侧共享最小化 / 最大化 / 关闭按钮。不能用 `SystemDecorations.None` 删除系统框架。 |
| macOS | 保留 `SystemDecorations.Full`、不透明窗口；扩展客户区，`PreferSystemChrome | OSXThickTitleBar`；原生 NSWindow 提供圆角、阴影和红绿灯。 | 主窗口标题行 52px、工具树上边距 34px；编辑器 44px，工具窗口 48px。 |
| Linux | 所有窗口保留 `SystemDecorations.Full`，不扩展客户区，不去掉 WM 装饰。窗口管理器提供边框及缩放，合成器决定阴影和圆角；能力不足时允许一致的原生直角。 | 原生标题栏；工具窗口隐藏重复的客户区标题，业务标题内容仍可作为工具栏显示。 |

窗口背景必须使用不透明的主题资源。内部卡片的 `CornerRadius` / `BoxShadow` 不代表整窗形状；不得将整窗设成透明，再用自绘圆角 Border 或额外阴影窗口替代原生形状。Windows 的逐像素透明或窗口 region 会失去 DWM 圆角资格。[Microsoft 圆角说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)说明了原生框架要求及最大化、贴靠和虚拟机中的限制。

Avalonia 11.3.22 的 [Win32 后端](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Windows/Avalonia.Win32/WindowImpl.cs)已经在扩展客户区时请求 DWM 圆角；不能把问题简单归结为框架没有圆角调用。它的 WinUI composition 路径可使用 0px DWM 边缘，应用统一入口显式保留 1px 边缘，并在 Avalonia 完成窗口设置后重申圆角请求。Windows / macOS 真实外观仍须在对应系统验收。

Mac OS 9 / Windows XP 皮肤通过 `Skin.Registration` 的 `PlatinumWindowFrame` / `WindowsXPWindowFrame` 绘制客户区经典框架，整窗仍使用统一原生宿主。Windows 扩展客户区 + `NoChrome`（标题高度提示 0）；macOS / Linux 保留原生标题栏。不得用 `SystemDecorations.None` 禁用外层系统阴影。

所有非模态工具（包括设置、帮助、词库、硬件测试、试听、结果窗口、播放器、看图和封面预览）使用 `WindowChrome.ShowIndependent(window)`：`ShowInTaskbar=true`，`CenterOwner` 改为 `CenterScreen`，无 Owner 的 `Show()`。媒体窗口可根据来源窗口的屏幕显式定位，但不设置所属关系。主窗口最小化或隐藏时，工具继续显示；用户可通过任务栏 / Dock 的窗口列表独立切换。同一应用在 Dock 是否合并为一个图标由系统决定。不要为层级感设置 Owner 或 Topmost；层级感由系统原生阴影体现。

若调用方需要等到工具关闭（例如释放设置服务或重新载入词库），使用 `ShowIndependentAsync`，通过 `Closed` 完成 Task，仍不禁用来源窗口。提交任务的非模态工具继续由 `ToolExecution.ShowAsync` 返回结果，它内部也使用独立 `Show()`。只有确实需要用户返回确认 / 编辑结果的提示、选项、编辑表单保留 `ShowDialog(owner)`；外观策略相同。业务队列、回调等上下文通过参数或应用的 `MainWindow` 获得，不通过工具窗口的 Owner 链查找。

标题行和队列栏留白支持 `BeginMoveDrag`，双击切换最大化；按钮、文本框、菜单、滑条、下拉及分隔条保留自身操作。固定大小 / 禁止最小化或最大化的工具窗口，其共享标题按钮也必须遵守对应能力。

主窗口、编辑器和工具窗口通过 `WindowChrome.HookDrag` 在窗口的 `PointerPressed` 隧道路由处理标题输入，不依赖透明标题控件成为事件源。顶部标题带从客户区顶端延伸到实际标题行底部，覆盖页面上边距和红绿灯两侧留白；主窗口队列栏按实际行范围包含外边距，不扩展到任务列表或侧栏正文。视觉树命中排除交互控件及其模板子元素，禁用控件也保留原点击区域；全屏不启动拖动。标题拆卸时移除窗口监听，换皮或替换内容不会留下重复监听。

拖动在左键按下事件中同步调用 Avalonia 的原生 `BeginMoveDrag`，先释放鼠标按下产生的隐式捕获，避免系统拖动循环接管释放事件后仍锁定原控件。依据当前依赖 Avalonia 11.3.22 的 [MouseDevice](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Base/Input/MouseDevice.cs)、[Window](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Controls/Window.cs) 和 [macOS WindowBaseImpl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/native/Avalonia.Native/src/OSX/WindowBaseImpl.mm)；macOS 的实际移动交给 `NSWindow.performWindowDragWithEvent`。

`AVAMEDIA_CHROME=mac|win|system` 控制布局选择；`mac` 在非 macOS 绘制占位红绿灯，只用于预览间距，不能改变真实平台的系统框架策略。Linux 的 mac 预览仍保留 WM 装饰；`system` 保留系统标题栏。`MACOS` 编译常量仅选择布局，平台 API 仍按实际操作系统调用。
