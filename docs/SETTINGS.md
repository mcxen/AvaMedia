# 选项设置与底层行为

参考：用户提供的 FormatFactory 5.10.0「高级」页及 Hardware Acceleration Testing 截图。实现日期：2026-10-05，Avalonia 11.3.22 / .NET 8。

## 界面范围

「选项」窗口默认打开高级页，保留选项 / 高级 / 内部三个标签、GPU / 系统 / 配置 / 照片 / 白金功能五个分组，以及默认 / 取消 / 应用 / 确定四个底部按钮。窗口客户区默认 926×800 DIP，最小 780×640；正文在小尺寸下滚动，底部操作固定。HA Test 客户区默认 580×392 DIP。

高级页沿用参考图的表单分组与功能位置，字体、输入高度、操作图标和底部按钮复用全局样式与当前皮肤。标签页采用当前皮肤的控件模板，窗口不再单独指定宋体或放大字号。图标是原创矢量；字体回退及 DPI 由平台决定。提供的截图未展示「选项」「内部」页，这两页承载 AvaMedia 现有的输出路径、外部工具、通知、并行任务与减少动效设置。

Bright Data 设备资源共享未集成 SDK。已核对官方 Windows 接入：需要 Bright SDK 账户的下载 API key、应用 APPID 和专属 `brd_config.json`，不能直接使用原软件的身份配置。参考位置保留禁用的未勾选控件，Tooltip 和内部页说明不可用；没有共享进程、服务调用或可保存的共享开关。该项属于尚未实现的底层差异。

## 高级设置

| 控件 | 默认 / 范围 | 实际行为 |
| --- | --- | --- |
| GPU 自动检测 | 开启 | 自动视频转换和录屏前检测当前 FFmpeg 的硬件编码能力；显式指定编码器和流复制跳过自动选择 |
| HA Test | 手动刷新 | 分别实际尝试 NVIDIA H.264 / H.265、AMD AMF H.264 / H.265、Intel QSV H.264 / H.265 / VP9 |
| 使用多线程 | 开启 | 控制转换进程的解码、编码及滤镜线程参数；关闭时传入 1 |
| 多线程数量 | 8 / 1–16 | 传入 FFmpeg 的输入和输出 `-threads`、`-filter_threads`、`-filter_complex_threads`；具体编码器是否支持多线程由其实现决定 |
| 重设 / 默认 | 设置草稿 | 恢复默认设置；点击应用 / 确定后才提交，保留主题选择；不会删除队列、预设或媒体 |
| JPG Quality | 90 / 1–100 | 用于未指定图片质量的 JPG 输出，值越高质量越高 |
| WebP Quality | 90 / 1–100 | 用于未指定图片质量的有损 WebP 输出，值越高质量越高 |

内部页的「同时执行任务数」控制队列并发，与每个进程的编码线程数不同。线程设置不是进程的 CPU 核心限制，不能保证所有 FFmpeg 内部工作只使用指定数量的系统线程。

## 硬件检测与回退

检测先读取编码器列表，再对存在的编码器实际编码 3 帧 1280×720 NV12 测试画面。仅列出编码器名称不代表驱动和硬件可用。读取列表及每次编码均有 6 秒超时；关闭检测窗口会取消剩余工作并终止对应进程。

弹窗逐行显示 `is supported` 或 `is NOT supported`；详细错误通过日志区 Tooltip 提供。不存在的编码器、初始化失败和超时均报告不支持。自动转换使用按 FFmpeg 路径与修改时间缓存的完整报告；HA Test 强制刷新。

MP4 / MOV / MKV / M4V / TS 的自动视频编码优先尝试 H.264，再尝试 H.265；同一家族按照 NVIDIA → AMF → QSV 排序。AVI / FLV 使用兼容的 H.264，WebM 使用 QSV VP9，MKV 还可尝试 QSV VP9。没有兼容候选的格式保留原软件策略。录屏接入相同的自动检测和回退流程。

输出配置按容器提供兼容的硬件选项，7 类检测能力均已接通：`h264_nvenc` / `hevc_nvenc`、`h264_amf` / `hevc_amf`、`h264_qsv` / `hevc_qsv` / `vp9_qsv`。MP4 / MOV / M4V 的 H.265 输出标记为 `hvc1`。检测成功不保证任意尺寸、滤镜和格式组合都可硬件编码。

自动硬件编码先写入输出目录中的唯一临时文件，成功后移动为目标输出。实际硬件编码失败则保留原因，继续尝试下一个兼容、已检测可用的编码器；全部失败才回退软件自动编码。成功、失败和取消均清理本次 GPU 临时文件。目标输出仍防覆盖，源文件保持原样。用户手动指定的硬件编码器保留直接错误反馈。

## 图片质量和兼容性

JPG 的 1–100 为项目定义的百分比刻度，换算为 FFmpeg MJPEG 的 `q:v = 2 + (100 − Quality) × 29 / 99`；不是对原产品 JPEG 算法或字节结果的复刻。WebP 明确使用 `libwebp` 有损模式，并同时设置 `-quality` 和 `-q:v`，避免当前 FFmpeg 的全局质量覆盖私有质量选项。

图片输出配置显示全局默认值，可为当前任务设置独立百分比质量并保存预设。任务或预设的 `ImageQuality` 优先于全局设置。旧队列没有新字段时继续加载；旧任务曾明确修改 `Quality` 且不等于旧默认 23 时保留原转换算法。打开这些旧参数但不修改质量，仍保留旧值；修改后使用新的百分比刻度。

全局默认在任务开始时应用到参数副本，运行过程不改写已保存的任务选项。旧设置文件缺少新字段时默认启用自动检测、8 线程、JPG / WebP 质量 90。

## 保存与取消

修改控件只编辑草稿，应用按钮从禁用转为可用；改回上次应用的值后恢复禁用。应用 / 确定会先验证全部字段，再提交设置；无效的目录、工具路径和数值不会部分提交。数值框直接校验当前输入文字，字母、小数、空白和越界值不会悄悄保存之前的数值。

应用立即保存并保留窗口，同时同步主窗口的输出目录、多线程、完成通知和减少动效。保存设置不重写任务队列，保存失败时保留草稿及之前的设置。确定保存后关闭。取消丢弃最后一次应用之后的修改；已经应用的值保留。设置保存到系统本地应用数据目录下的 `AvaMedia/settings.json`。

## 验证

```powershell
& .\.tools\dotnet\dotnet.exe build AvaMedia.sln -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.SettingsTests -c Release --no-build
```

2026-10-05 本机通过 **114 项设置检查，生成 15 个实际输出**。输出数量和硬件分支随机器能力变化。验证包括：

- JPG / WebP 低高质量的文件大小、解码像素误差与任务覆盖，全局线程参数、配置持久化及旧格式兼容。
- 实际硬件检测、本机 NVIDIA H.264 / H.265 成功编码、FLV / TS / M4V 的自动 GPU 实际输出；模拟错误能力报告后，实际 FFmpeg 失败并继续使用 H.265，或者全部失败后回退软件；临时文件清理、关闭自动检测和流复制跳过检测。
- 7 类硬件编码器在对应输出配置中保持选中参数；不兼容容器排除候选；录屏对不存在的指定窗口进行失败路径验证，不采集真实桌面。
- 应用、取消、草稿重置、恢复原值、非法数值文字和保存失败；主窗口即时保存、任务队列不被设置提交重写与工具栏同步；图片输出质量编辑与预设恢复。
- 浅 / 深主题、最小尺寸的底部按钮边界、HA Test 运行与关闭取消；所有测试使用独立状态目录及生成的媒体。

报告和 PNG 位于 `artifacts/settings-20261005-214504/`。另外通过 UI 30 项、功能 39 项 / 19 个输出、音频 32 项、快速剪辑 52 项 / 5 个输出，以及综合冒烟 93 项 / 60 个输出 / 53 个入口回归检查。综合录屏生成可读视频；AMD / Intel 硬件实际成功编码、原生高 DPI、多平台和屏幕阅读器完整验收尚未完成。

## 实现与来源

界面：`SettingsWindow.axaml` / `.axaml.cs`、`HardwareTestWindow.cs`、`Controls/SettingsIcon.cs`。底层：`HardwareAcceleration.cs`、`SettingsPolicy.cs`、`MediaEngine.cs`、`Models.cs`。图片任务参数：`OptionsWindow.cs` / `ConvertWindow.cs`。

- [FFmpeg 命令行文档](https://ffmpeg.org/ffmpeg.html)：输入 / 输出选项作用域与滤镜线程。
- [FFmpeg 编码器文档](https://ffmpeg.org/ffmpeg-codecs.html)：编码线程及 libwebp 参数。
- [FFmpeg libwebp 公共实现](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/libwebpenc_common.c)：全局质量对私有质量的覆盖。
- [Avalonia 11.3.22 TabControl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Themes.Simple/Controls/TabControl.xaml)、[TabItem](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Themes.Simple/Controls/TabItem.xaml)：锁定版本控件模板结构。
- [Bright SDK Windows 官方示例](https://github.com/BrightSDK/bright-sdk-integration-example-windows)、[Windows SDK 官方接入指南](https://help.bright-sdk.com/hc/en-us/articles/16679042461329-Windows-SDK-Integration-Guide-C-C)：SDK 下载认证、APPID 和应用身份配置。
