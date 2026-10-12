# 窗口框架（浅色 / 深色皮肤）

实现：`src/AvaMedia.Desktop/MainWindow.WindowChrome.cs`（由 `MainWindow.ModernChrome.cs` 的 `ApplyChrome` 调用）。
Mac OS 9 / Windows XP 皮肤保持系统窗口装饰和经典菜单栏、工具栏，不受本节影响。

| 平台 | 行为 |
|---|---|
| macOS | `ExtendClientAreaToDecorationsHint` + `PreferSystemChrome \| OSXThickTitleBar`；原生红绿灯位于浮动侧栏左上角，工具树顶部留出 34px，标题「任务」与红绿灯同一行（标题行高 52）。 |
| Windows | 扩展客户区 + `NoChrome`，标题行最右侧是自绘的最小化 / 最大化 / 关闭（46×32，关闭悬停为 #C42B1C）；系统仍提供边框缩放与贴靠。 |
| Linux | 布局同 Windows。X11 不支持扩展进 WM 装饰，因此改为 `SystemDecorations.None`，并在窗口四边和四角放 6px 透明缩放手柄（`BeginResizeDrag`）。 |

通用：标题行与「任务列表」行空白处按下拖动窗口（`BeginMoveDrag`），双击切换最大化；按钮、输入框上的点击不触发拖动。

选择方式：
- 运行时按 `OperatingSystem.IsMacOS()` 判断；定义 `MACOS` 编译常量（单独打 macOS 包时）则直接固定为 macOS 布局。
- 环境变量 `AVAMEDIA_CHROME=mac|win|system` 可覆盖：`mac` 在非 macOS 上画三个占位红绿灯用于预览间距；`system` 回退为系统窗口装饰（WM 装饰异常时使用）。
