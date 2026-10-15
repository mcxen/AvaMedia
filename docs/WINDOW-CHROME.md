# 桌面窗口框架

现代皮肤（浅色、深色和跟随系统）的所有普通窗口使用 Avalonia 标准 `Window` 和系统标题栏，覆盖主窗口、编辑器、工具、设置、帮助、临时对话框以及非全屏播放器 / 看图窗口。

`Controls/WindowChrome.Platform.cs` 统一设置 `SystemDecorations.Full`、`ExtendClientAreaToDecorationsHint=false`、标题高度提示 -1 与默认 ChromeHints。Windows、macOS 与 Linux 的标题文字、窗口按钮、拖动、双击缩放、边框、圆角、贴靠和阴影交给系统。窗口背景仍引用不透明主题资源，不加自绘外框，不重新定位 macOS 红绿灯，不绘制占位按钮，不再读取 `AVAMEDIA_CHROME`。

`Skin.Registration` 在打开、换皮、内容与窗口状态变化后应用策略。现代皮肤直接呈现业务内容，不自动包一层客户区标题栏；`WindowChrome.WrapDialog` 只排列原内容与显式传入的业务工具栏，不绘制窗口标题或窗口按钮。设置分类标题、编辑模式、媒体信息、撤销 / 重做、任务状态与动作仍属于业务内容，不承担窗口拖动和最大化。

Mac OS 9 / Windows XP 继续由 `PlatinumWindowFrame` / `WindowsXPWindowFrame` 绘制现有经典外框与控件。Windows / macOS 的经典模式保留 `SystemDecorations.Full`，扩展客户区并使用 `NoChrome`、标题高度提示 0，维持现有系统宿主与阴影；Linux 保留 WM 装饰。切回现代皮肤时释放经典外框、还原原内容与系统标题栏；退出全屏后也按当前皮肤恢复。经典外框自己的拖动、收起、缩放和关闭逻辑保留。

全局窗口策略只处理本应用定义的窗口和应用创建的普通对话框。Avalonia 的 Windows 托盘菜单内部宿主必须保留框架自身的定位、无装饰与布局，禁止套入业务皮肤、位置记忆、开窗动效或应用菜单。

所有非模态工具（包括设置、帮助、词库、硬件测试、试听、结果窗口、播放器、看图和封面预览）使用 `WindowChrome.ShowIndependent(window)`：`ShowInTaskbar=true`，`CenterOwner` 改为 `CenterScreen`，无 Owner 的 `Show()`。媒体窗口可根据来源窗口的屏幕显式定位，但不设置所属关系。主窗口最小化或隐藏时，工具继续显示；用户可通过任务栏 / Dock 的窗口列表独立切换。同一应用在 Dock 是否合并为一个图标由系统决定。不要为层级感设置 Owner 或 Topmost；层级感由系统原生阴影体现。

普通工具优先恢复仍在有效屏幕内的已保存位置；首次打开优先主窗口所在屏幕的右侧、其次左侧，放不下时在该屏幕工作区居中并裁限。不得回退到屏幕左上角或自动投放到其他屏幕。未显式设置 `WindowStartupLocation` 的独立窗口同样接入该策略；显式 Manual 位置及模态表单保留调用方策略。

定位使用 `DesktopScaling` 计算来源窗口的桌面边界，目标尺寸与间距按目标 `Screen.Scaling` 换算，不使用 `RenderScaling`。Avalonia 11.3.22 的 macOS 后端以逻辑点表示屏幕坐标，桌面缩放为 1，而 Retina 绘制倍率通常为 2；混用会将主窗口占地翻倍，错误地判断没有相邻空间。依据 [Window 的定位实现](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Controls/Window.cs)、[TopLevelImpl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Native/TopLevelImpl.cs) 与 [Screens.mm](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/native/Avalonia.Native/src/OSX/Screens.mm)。

若调用方需要等到工具关闭（例如释放设置服务或重新载入词库），使用 `ShowIndependentAsync`，通过 `Closed` 完成 Task，仍不禁用来源窗口。提交任务的非模态工具继续由 `ToolExecution.ShowAsync` 返回结果，它内部也使用独立 `Show()`。只有确实需要用户返回确认 / 编辑结果的提示、选项、编辑表单保留 `ShowDialog(owner)`；外观策略相同。业务队列、回调等上下文通过参数或应用的 `MainWindow` 获得，不通过工具窗口的 Owner 链查找。


本次只做桌面项目编译与相关静态检查；未运行跨平台窗口交互、截图或皮肤切换验收。
