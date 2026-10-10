# FFmpeg 体积比较

本文保留 2026-10-06 的旧工具测量。当前发布使用固定源码的 GPL FFmpeg，并内置到安装包；现行配方与许可见 [FFmpeg 构建](../FFMPEG-MACOS.md) 和 [第三方声明](../../THIRD-PARTY-NOTICES.md)。下文的 BtbN 安装方式和“未嵌入”描述仅属于当日版本。

2026-10-06 实测。比较对象为本机 FormatFactory X64 5.10.0、原开发用 BtbN 静态 LGPL 构建与新安装的 BtbN 共享 LGPL 构建。MiB = 1,048,576 字节；原始文件尺寸与能力对照见 [测量记录](../engine/ffmpeg-size-20261006.json)。

| 对象 | FFmpeg 程序 | 探测程序 | 媒体运行文件合计 |
| --- | ---: | ---: | ---: |
| FormatFactory 5.10.0 | 0.30 MiB | 使用自己的探测组件，此处未计入 | ffmpeg.exe + 8 个 FFmpeg DLL：44.78 MiB |
| 原 BtbN 静态构建 | 131.83 MiB | ffprobe：131.63 MiB | 两个程序：263.46 MiB；另加未使用的 ffplay 后为 398.66 MiB |
| 新 BtbN 共享构建 | 0.53 MiB | ffprobe：0.22 MiB | 两个程序 + 7 个 DLL：153.71 MiB |

格式工厂将编码、滤镜和容器实现放在旁边的 avcodec.dll、avfilter.dll、avformat.dll 等共享库中，不能只用 ffmpeg.exe 的 0.30 MiB 代表整个引擎。其本地版本为 2021 年的 N-104384-g374f2ac370，编译配置包含 `--disable-static --enable-shared --enable-small`，编译器为 GCC 8.3.0。

新工具仍比格式工厂旧引擎大：当前是 2026 年的 FFmpeg，采用 GCC 16.2.0，并启用了更多外部组件，例如 libjxl、libplacebo、libvmaf、libsvtav1 和 librav1e。共享构建不会自动把这些库的代码体积裁掉。BtbN 官方同时提供 static 与 shared 变体；共享变体沿用相同依赖集合。[BtbN 构建说明](https://github.com/BtbN/FFmpeg-Builds#targets-variants-and-addins)

`Install-MediaTools.ps1` 已改为安装 `win64-lgpl-shared`，仅保留 ffmpeg.exe、ffprobe.exe 和配套 DLL。播放器通过自己的 FFmpeg 解码与 Avalonia 呈现工作，无需 ffplay。下载与解压使用临时目录，结束后清理；安装目录保存构建配置、许可证、文件尺寸与 SHA256。相比原来实际使用的两个静态程序减少 **41.7%**；相比包含 ffplay 的完整 bin 目录减少 **61.4%**。下载 ZIP 从约 169.47 MiB 降至 75.79 MiB。

对照静态 `N-127197-gf0c2c00a62-20261004` 与共享 `N-127203-ga35c879992-20261005`，编码器、解码器、滤镜、格式、协议和设备列表全部逐行一致。新安装的共享版通过全部 18 套客户端测试，包含实际转换、预览、字幕、录屏、下载、批量处理、设置与消融。两次构建不是同一个源码提交，以上结论限定于实际列举能力及这些功能测试。

FFmpeg 与 FFprobe 仍作为外部可替换工具安装，未嵌入客户端应用包；格式工厂的专有文件没有被复制。以上 Windows 工具方案使用不启用 gpl/nonfree 的 LGPL 构建，原应用许可证保持 AGPL-3.0-only。[FFmpeg 许可说明](https://ffmpeg.org/legal.html)

macOS ARM64 使用另行定制的源码构建配方，按客户端能力选取依赖，并输出独立运行包、对应源码和体积报告。它保留 x264 / x265，许可为 GPL-3.0-or-later；尚未取得原生构建体积，不套用 Windows 的测量数字。详见 [macOS 定制方案](../FFMPEG-MACOS.md)。
