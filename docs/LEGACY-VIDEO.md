# 3GP 与旧视频格式

维护日期：2026-10-06。

旧视频与现代视频共用播放器、后台文件夹播放列表、拖入路由、格式转换、视频压缩、剪辑、裁剪 / 旋转、提取音频和批量工具。`VideoFormats.InputExtensions` 是共同输入名单，文件内容由 FFprobe / FFmpeg 实际探测与解码，不根据扩展名推测编码。

| 输入类型 | 扩展名 | 主要解码能力 |
| --- | --- | --- |
| 早期手机视频 | 3gp、3g2、3gpp、3gpp2 | H.263、MPEG-4、H.264，AMR-NB / WB、AAC、QCELP |
| RealMedia | rm、rmvb | RealVideo 1–4，RealAudio、Cook、SIPR |
| AVI 与 Windows Media | avi、divx、wmv、asf | MPEG-4 / DivX、WMV 1–3、VC-1、WMA |
| Flash 与旧 QuickTime | flv、f4v、mov、qt | Sorenson、VP6、Cinepak、H.264，原生对应音频解码 |
| VCD / DVD 与摄像机 | mpg、mpeg、mpe、dat、vob、vro、mod、tod、dv，及 m1v / m2v | MPEG-1 / 2、DV；DAT 指包含 MPEG 视频的 VCD 文件 |
| 其他旧视频与动画 | mjpeg、mjpg、amv、nsv、fli、flc | MJPEG、AMV、NSV 的视频流及 FLIC |

这些容器与解码能力依据 [FFmpeg 官方格式 / 编码支持表](https://ffmpeg.org/general.html)；早期手机语音使用其原生 [AMR-NB 解码器](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/libavcodec/amrnbdec.c)，无需新增第三方编码库。自定义精简 FFmpeg 可能缺少解码器，客户端显示实际错误；加密或损坏的文件不因扩展名在名单中而保证可播放。

## 3GP / 3G2 输出

在“视频格式转换”的输出格式选择 3GP 或 3G2，默认 MPEG-4 视频 + AAC 音频；可选择软件或当前平台的 H.264 编码器。配置不列出 HEVC、AV1、Opus 等不适用编码，底层同时校验；按现有设置处理尺寸、质量、区间、音轨和字幕，文本字幕输出 mov_text，启用 faststart。这些组合依据 [FFmpeg 3GP / 3G2 复用器的编码映射](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/libavformat/movenc.c)。未新增 AMR 编码器；设备是否接收某种编码仍取决于其能力，3GP 容器不等于所有旧手机都能播放。

输入名单与原格式导出名单分开。现有支持的 3GP / 3G2 可选择 Fast Copy，源轨道须被目标容器接受；RMVB、VCD DAT、原始视频等扩展名不直接作为原格式输出，选择 MP4 / MKV 重新编码。原属性旋转 / 裁剪仍受源编码器尺寸等约束，可改为指定格式导出。

## 构建与检查

`scripts/legacy-video-capabilities.json` 统一声明所需原生解码器、解复用器与 3GP / 3G2 编码 / 复用能力。Windows 工具安装在复制运行文件前检查；macOS 定制引擎验证检查同一声明，对应源码包同时包含该文件。既有 macOS 构建启用 FFmpeg 内置组件，无需新增外部库。许可证继续沿用 [第三方声明](../THIRD-PARTY-NOTICES.md)；客户端改动为 AGPL-3.0-only。

本次按工程约定完成相关项目编译、修改脚本 / JSON 静态检查和本地 FFmpeg 能力清单核对，未运行旧视频文件播放、媒体编码输出或 macOS 实机回归，不把能力名单核对当成媒体验收。
