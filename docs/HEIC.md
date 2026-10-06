# HEIC / HEIF 与苹果照片

维护日期：2026-10-06。

拖入 `.heic` / `.heif` 会识别为图片，可选择图片压缩、格式转换、缩放 / 旋转、图片合成 PDF、媒体信息或文件校验。图片压缩的添加文件与添加文件夹共用同一支持范围；批量混合 JPEG / PNG / WebP / BMP 和 HEIC 时使用同一设置与真实前后对比。

## 主图读取与输出

`HeifImage` 根据 FFprobe 的默认视频流或默认 Tile Grid 选择静态主图，读取显示尺寸与方向。多个 HEVC 图块通过 FFmpeg 内部合成图的 `[0:g:N]` 输出连接到后续滤镜，不能用 `0:v:0` 或普通 `0:g:N` 映射替代；前者只取首块，后者选择组内各个流。单块派生图没有内部合成图，按组信息裁剪、旋转或镜像，再连接编辑滤镜。该选择用于缩略图、压缩预览与队列编码、格式转换、缩放 / 旋转和 PDF 预处理。

图片压缩输出 JPEG、WebP 或 PNG，只保存严格小于原文件的结果并保留原图；转换工具可输出其现有图片格式。高于 8 位的主图导出无损 PNG 时使用 16 位 RGB / RGBA，禁止把降为 8 位的 WebP 当成无损保留源位深。位深保留不等于保留 HEIC 的所有拍摄信息或 HDR 显示效果。

当前处理静态主图，不提供 HEIC 编码，不导出 Live Photo 视频、深度图、辅助 Alpha 或 HDR 增益图；HEIF 动画序列明确报错。常规压缩移除拍摄元数据，JPEG 的透明区域填白。已有 [图片压缩](IMAGE-COMPRESSION.md) 的实际编码、停止、防覆盖及体积约束继续生效。

## macOS 的后台预览

`AppleImageIO` 延迟加载系统 ImageIO / CoreFoundation，通过主图索引读取尺寸、位深与方向。原生调用在后台任务执行，图片路由按 320×260 上限生成带方向转换的缩略图，保持比例、不放大，不先解码一张全尺寸位图。返回内存 PNG 后，Avalonia 位图解码也在后台执行。该路径同时覆盖 JPEG / PNG / TIFF 的不填边预览；Windows 和需要填边的预览仍使用 FFmpeg。

图片压缩保留全尺寸原图供 1:1 对比，调整质量或尺寸时只替换实际编码结果，复用当前原图；切换文件、关闭窗口释放位图，迟到的请求不得覆盖当前图片。路由沿用两项并发和生命周期取消；原生单次调用中途不能强制中断，调用前后检查取消并丢弃迟到结果。编码仍使用 FFmpeg，确保窗口和任务队列共用 `IImageCompressor`。

这是减少进程创建与重复解码的实现改进，尚未公布 Apple Silicon 的时间、CPU 或内存提升比例。

## 开源方案与引擎要求

- [libheif](https://github.com/strukturag/libheif) 提供 HEIF 容器与多种编解码器接入；库使用 LGPL，具体编解码器另有许可。现有 FFmpeg 与 macOS ImageIO 已覆盖此次主图需求，因此未新增 libheif 本地依赖。
- [SDWebImage 的 ImageIO 解码器](https://github.com/SDWebImage/SDWebImage/blob/master/SDWebImage/Core/SDImageIOAnimatedCoder.m) 提供按目标尺寸下采样的实现参考，项目采用 [MIT](https://github.com/SDWebImage/SDWebImage/blob/master/LICENSE)。未复制代码或引入包。
- [FFmpeg 8.1.3 的 Tile Grid 处理](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/fftools/ffmpeg_demux.c) 提供多块合成及显示裁剪 / 方向处理；[映射实现](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/fftools/ffmpeg_mux_init.c) 区分合成图输出和输入流。定制 macOS 引擎使用 8.1.3，要求 `xstack`、HEVC 解码和 FFprobe stream_groups；Windows 安装脚本的当前构建也包含这些能力，自定义旧工具需更新。
- 原生 API 依据 Apple 的 [主图索引](https://developer.apple.com/documentation/imageio/cgimagesourcegetprimaryimageindex(_:))、[缩略图](https://developer.apple.com/documentation/imageio/cgimagesourcecreatethumbnailatindex(_:_:_:)) 与 [方向转换选项](https://developer.apple.com/documentation/imageio/kcgimagesourcecreatethumbnailwithtransform)。系统框架不随应用分发。

原创集成代码采用 AGPL-3.0-only，既有 FFmpeg 分发义务见 [第三方声明](../THIRD-PARTY-NOTICES.md)。本次无新增 NuGet 或原生第三方库。

## 当前检查范围

受影响的 Core / Desktop 项目在隔离副本完成 Release 编译，零警告、零错误；语言 JSON、修改脚本与差异完成必要静态检查。按工程约定未新增测试、未运行 HEIC 媒体输出验证；当前 Windows 主机也不能替代 macOS ImageIO 实机验证。
