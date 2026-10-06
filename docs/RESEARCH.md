# FormatFactory 能力与复用调研

调研日期：2026-10-05。界面基准：用户提供的 FormatFactory X64 5.10.0 主窗口和剪辑窗口。电脑插件枚举到了该软件的实际窗口；窗口捕获两次超时，因此未声称完成所有窗口的现场操作验证。

## 原产品能力

官网列出音视频转换、剪辑、连接、分离、混流、裁剪和去水印，图片转换与缩放旋转，BD/DVD/CD 提取、PDF 文本及 Office 转换、压缩包解压、屏幕录像、视频下载。该页面是当前产品介绍，不应将新增能力误认为 5.10.0 就全部拥有。

来源：[官方产品页](https://www.pcfreetime.com/formatfactory/index.php)。

5.10.0 更新于 2022-01-31，涉及音频封面、QSV 解码、MKV 字幕保留、输出预设和流分离范围选择。5.9 记录了画面优化、导帧相关改进以及更多音视频滤镜。后续版本逐渐增加更多硬件编码、预设；5.21 将下载模块换为 yt-dlp；5.22 增加 CAJ/PDF、文档 Markdown 和 M3U8。它们属于扩展调研，并不假定已在本客户端实现。

来源：[官方更新历史](https://www.pcfreetime.com/formatfactory/changelog.php?language=en)。

## 已采用的复用路径

| 能力 | 组件 / 实现 | 许可与复用方式 |
|---|---|---|
| 跨平台窗口、布局、控件 | Avalonia 11.3.22 | MIT；保留版权与许可 |
| 转码、探测、缩略图、播放解码、滤镜 | 外部 FFmpeg / FFprobe | 独立进程 `ArgumentList`；用户可替换；按实际构建的 LGPL/GPL 条款处理 |
| Windows 预览声音 | NAudio 2.2.1 | MIT；保留许可 |
| PDF 合并、拆分、图片 / 文本生成 PDF | PDFsharp 6.2.4 | MIT；系统字体在用户机器上加载，没有分发字体文件 |
| PDF 文字提取 | PdfPig 0.1.13 | Apache-2.0；附许可证与适用 NOTICE |
| 视频网站下载 | 外部 yt-dlp | 项目源代码为 Unlicense；官方打包可执行文件包含其他许可组件；成品不内置它 |
| ZIP、SHA256、队列持久化、DOCX/XLSX 文本容器 | .NET 与独立 C# | .NET 版权声明；客户端原创 AGPL-3.0-only |

来源：[Avalonia 许可](https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md)、[FFmpeg 官方法律说明](https://ffmpeg.org/legal.html)、[FFmpeg 滤镜文档](https://ffmpeg.org/ffmpeg-filters.html)、[PDFsharp 许可](https://github.com/empira/PDFsharp/blob/master/LICENSE)、[PdfPig 许可](https://github.com/UglyToad/PdfPig/blob/master/LICENSE)、[NAudio 许可](https://github.com/naudio/NAudio/blob/release/2.x/license.txt)、[yt-dlp 第三方许可](https://github.com/yt-dlp/yt-dlp/blob/master/THIRD_PARTY_LICENSES.txt)。

FormatFactory 官网 EULA 只说明其使用了部分 LGPL 模块；免费使用不代表专有客户端、图标、FTCore、FTMedia、FFUILib 等代码可任意复制。工程不复制这些资源。Qt/QtAV 可供独立研究，但本项目使用 Avalonia，因此没有引入 Qt/QtAV。

来源：[FormatFactory EULA](https://www.pcfreetime.com/formatfactory/eula_privacy_policy.php?language=en)。

## 本地引擎核查

原安装目录中的 FFmpeg：`N-104384-g374f2ac370`，配置包含 `--enable-gpl --enable-version3 --enable-nonfree`。该文件及配套 DLL 没有被复制到客户端，也没有用于功能验证。

开发验证改用 BtbN 独立 `win64-lgpl` 构建：`N-127197-gf0c2c00a62-20261004`，配置启用 version3，禁用 libx264/libx265，不启用 gpl/nonfree。FFmpeg 的版本、完整配置与 SHA256 保存在 `artifacts/engine-audit.txt`。BtbN 的构建脚本许可不代表其所有输出组件均是 MIT。

2026-10-06 改用 `win64-lgpl-shared`，避免 ffmpeg 与 ffprobe 重复静态链接媒体库。格式工厂采用共享库和 `--enable-small`，其 FFmpeg 运行文件约 44.78 MiB；本项目新工具运行文件约 153.71 MiB，能力列表一致且 18 套功能测试通过。详见 [体积比较](FFMPEG-SIZE.md)。

来源：[BtbN 构建说明](https://github.com/BtbN/FFmpeg-Builds)。

## 未纳入的复用候选

RAR/7z 可考虑外部 7-Zip，OCR 可考虑 Tesseract，PDF 光栅化可考虑 PDFium，专业播放可考虑 libVLC。应分别核查具体版本、打包许可、源代码与 NOTICE，再实现和验证。尚未引入这些组件，界面没有将它们伪装成已经可用的功能。
