# Mac OS 9 · Platinum 皮肤

从主窗口“显示 → 皮肤 → Mac OS 9 · Platinum”切换。浅色、深色和 Platinum 采用相同的菜单层级，语言设置位于“显示 → 语言”，偏好设置位于“编辑”。选择随设置保存，重启后恢复；已打开的窗口和后续弹窗同步切换。

## 界面基础

- `Skin.MacOS9` 是继承 Light 的 Avalonia ThemeVariant。`Platinum.axaml` 定义灰阶、文字、选中、工具栏和状态栏资源，`PlatinumControls.axaml` 定义控件主题。
- 窗口采用黑色外框、白色内高光、六组条纹、左侧关闭框、右侧缩放和 WindowShade 收起框；非活动窗口隐藏条纹，标题与控件变灰。右下角有 Grow Box，边缘也能调整大小。
- 双击标题栏或点击收起框，将窗口折叠为标题栏；再次操作恢复高度、最小高度和缩放状态。关闭、缩放、最小化遵守原有窗口权限。播放器使用同一套框架，全屏时移除装饰而保留 Platinum 控件。
- 普通按钮使用经典圆角斜边，默认确认按钮带黑色外环；工具栏采用较小的方形斜边按钮。按下立即凹陷，复选框、单选框、滚动条抓手与播放按钮用像素对齐的图形绘制。
- 下拉框使用斜边箭头及硬边阴影弹出列表；页签使用阶梯轮廓；滚动条和滑块使用淡紫色抓手；进度条使用经典淡紫色立体填充。输入框保留文本编辑、错误提示与键盘行为。
- 按用户要求，主窗口按“标题栏 → 菜单栏 → 操作控制栏 → 任务区”排列；所有皮肤的菜单均按“文件、编辑、显示、转换、窗口、帮助”组织。偏好设置在编辑菜单内，皮肤与语言在显示菜单内。切换皮肤只改变呈现，不再移动或重新组织菜单项；原有命令和快捷键继续有效。Platinum 菜单有黑边、两像素实阴影、蓝紫色反白选中与灰白分隔线；右键菜单、子菜单和提示气泡使用相同资源。
- 换回其他皮肤时恢复系统装饰和原来的内容树，保留输入值、选择、播放和队列状态。关闭窗口时释放注册；减少动效偏好继续生效。
- 内置 Fusion Pixel 12px Prop zh-Hans 开源字体，统一英文、数字和简体中文的像素风格；采用别名文字渲染与逻辑像素布局，随 DPI 缩放。没有嵌入 Apple 字体或背景图片。完整字体许可、贡献者声明与哈希见 `licenses/fonts/fusion-pixel/`。
- Platinum 的界面变化立即反馈，取消现代按钮缩放及窗口淡入；浅色、深色继续遵守减少动效设置。

## 专用比例与宿主适配

| 项目 | Platinum 逻辑像素 |
|---|---:|
| 正文、标题及数字字号 | 12 |
| 菜单栏 / 标题栏 | 20 / 21 |
| 工具栏 / 状态栏 | 34 / 20 |
| 功能图标 / 功能行 | 32 / 74 |
| 输入框 / 确认按钮 | 22 / 26 |
| 滚动条 | 16 |

这是运行于现代系统的应用皮肤。菜单位于标题栏下方、操作控制栏上方，系统文件选择器、系统托盘和任务栏仍由宿主系统提供。切换皮肤不重新创建媒体业务视图，已填写选项、队列和播放对象继续使用；菜单保持同一层级和位置。旧弹出菜单的容器在模板更换时释放，避免再次打开时出现重复父级。

设计依据是 Apple 的 [Mac OS 8 Human Interface Guidelines](https://dev.os9.ca/techpubs/mac/pdf/HIGOS8Guidelines.pdf) 中 Platinum 窗口、菜单、控件及状态规范。Mac OS 9 沿用这一套视觉语言。

## 开源调研与复用

| 实现 | 调研结论 | 使用方式 |
|---|---|---|
| [npjg/classic.css](https://github.com/npjg/classic.css) | MIT，包含 Classic Mac 的立体边缘与灰阶实现 | 采用边缘配色排列，完整版权许可存于 `licenses/upstream/classic-css-MIT.txt`；在 Avalonia 中实现 |
| [eyaltoledano/platinum](https://github.com/eyaltoledano/platinum) | 浏览器框架，包含窗口、外观管理和控件的 Mac OS 8/9 Platinum 结构 | 参考组件组织；源码与素材未引入客户端 |
| [Avalonia ThemeVariant](https://docs.avaloniaui.net/docs/styling/theme-variants) / SimpleTheme | 现有 Avalonia 11.3.22 资源系统与 MIT 控件主题 | 继承默认控件的文本编辑、弹出选择、焦点和键盘行为；皮肤层负责视觉 |

## 验证

2026-10-06 的菜单位置调整通过受影响桌面项目编译与隔离界面检查：主窗口菜单位于操作工具栏下方，所有主菜单和皮肤子菜单正常展开；在弹出菜单打开时往返切换浅色、深色和 Platinum，菜单仍保持连接并恢复相应位置；标题栏收起与恢复正常。记录在 `artifacts/platinum-menu-audit/audit.log`，新版程序在 `artifacts/platinum-menu-below-toolbar/win-x64/`，`Start-AvaMedia.cmd` 指向这一版。下面的截图与回归记录属于旧版本，不作为本次验收证据。

```powershell
./.tools/dotnet/dotnet.exe run --project tests/AvaMedia.SkinTests -c Release
```

输出 `artifacts/skins-*/report.json` 与主窗口、配置窗口、组件的 PNG。验证切换与恢复、持久化、实际控件状态、焦点与按下效果、弹窗、页签，以及标题栏按钮。启动参数 `--macos9` 可用于原生窗口的隔离截图验收。

2026-10-05 在干净的 Git 提交 `6ea090d` 上通过 11 套回归，其中包含 30 项皮肤交互检查、18 组媒体消融对照（35 项检查 / 20 个输出）。同时启动原生播放器，验证实际解码、剪辑区间、缩略图与裁剪控件；最终独立运行 Windows 发行包并生成主窗口截图。

验收记录与截图在 `artifacts/platinum-6ea090d/`。Windows 客户端在 `artifacts/release/1.0.2-platinum/win-x64/AvaMedia.Desktop.exe`，便携包和对应 Git 源码分别是 `artifacts/AvaMedia-1.0.2-platinum-win-x64.zip` 与 `artifacts/AvaMedia-1.0.2-platinum-source.zip`。哈希和验证提交见 [发行记录](releases/1.0.2-platinum.json)。
