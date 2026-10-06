# Mac OS 9 · Platinum 皮肤

从主窗口“皮肤 → Mac OS 9 · Platinum”切换。选择随设置保存，重启后恢复；已打开的窗口和后续弹窗同步切换。浅色、深色和 Platinum 共用业务控件及数据。

主窗口按用户要求排列为“标题栏 → 操作工具栏 → 菜单栏 → 任务区”。换回浅色或深色时恢复原来的菜单位置。切换模板时只释放实际被替换的弹出菜单容器，保留仍在使用的菜单树。

## 界面基础

- `Skin.MacOS9` 是继承 Light 的 Avalonia ThemeVariant。`Platinum.axaml` 定义灰阶、文字、选中、工具栏和状态栏资源，`PlatinumControls.axaml` 定义控件主题。
- 按钮用分辨率独立的立体边缘，按下立即凹陷，键盘焦点有清晰边框；输入框、下拉框、复选框、页签、滚动条、滑块和进度条沿用原生 Avalonia 的交互实现。
- 条纹标题栏支持标题更新、拖动、双击缩放、边缘调整大小、最小化、缩放和关闭。尊重窗口的 CanResize / CanMaximize / CanMinimize 设置。
- 换回其他皮肤时恢复系统装饰和原来的内容树，保留输入值、选择、播放和队列状态。关闭窗口时释放注册；减少动效偏好继续生效。
- 使用设备已有字体，中文保持可读。线条和控件用逻辑坐标布局，随 DPI 缩放；没有在程序中嵌入 Apple 字体或背景图片。

## 开源调研与复用

| 实现 | 调研结论 | 使用方式 |
|---|---|---|
| [npjg/classic.css](https://github.com/npjg/classic.css) | MIT，包含 Classic Mac 的立体边缘与灰阶实现 | 采用边缘配色排列，完整版权许可存于 `licenses/upstream/classic-css-MIT.txt`；在 Avalonia 中实现 |
| [eyaltoledano/platinum](https://github.com/eyaltoledano/platinum) | 浏览器框架，包含窗口、外观管理和控件的 Mac OS 8/9 Platinum 结构 | 参考组件组织；源码与素材未引入客户端 |
| [Avalonia ThemeVariant](https://docs.avaloniaui.net/docs/styling/theme-variants) / SimpleTheme | 现有 Avalonia 11.3.22 资源系统与 MIT 控件主题 | 继承默认控件的文本编辑、弹出选择、焦点和键盘行为；皮肤层负责视觉 |

## 验证

```powershell
./.tools/dotnet/dotnet.exe run --project tests/AvaMedia.SkinTests -c Release
```

输出 `artifacts/skins-*/report.json` 与主窗口、配置窗口、组件的 PNG。验证切换与恢复、持久化、实际控件状态、焦点与按下效果、弹窗、页签，以及标题栏按钮。启动参数 `--macos9` 可用于原生窗口的隔离截图验收。

2026-10-05 在干净的 Git 提交 `6ea090d` 上通过 11 套回归，其中包含 30 项皮肤交互检查、18 组媒体消融对照（35 项检查 / 20 个输出）。同时启动原生播放器，验证实际解码、剪辑区间、缩略图与裁剪控件；最终独立运行 Windows 发行包并生成主窗口截图。

验收记录与截图在 `artifacts/platinum-6ea090d/`。Windows 客户端在 `artifacts/release/1.0.2-platinum/win-x64/AvaMedia.Desktop.exe`，便携包和对应 Git 源码分别是 `artifacts/AvaMedia-1.0.2-platinum-win-x64.zip` 与 `artifacts/AvaMedia-1.0.2-platinum-source.zip`。哈希和验证提交见 [发行记录](releases/1.0.2-platinum.json)。
