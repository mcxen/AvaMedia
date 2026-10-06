# 视频压缩

主窗口把 MP4、MKV、WebM、GIF 与其它视频格式入口合并为“格式转换”：在窗口中选择输出格式，包含 MP4、MKV、MOV、WebM、AVI、FLV、WMV、MPG、TS、GIF。格式转换用来改变容器、编码和媒体参数，不以缩小体积为目标。

“视频压缩”是独立入口，替换原先只设置质量值的“优化”。流程为添加视频 → 设置目标 → 查看逐文件预估 → 加入导出队列 → 主窗口开始。支持多选和添加文件夹，同一路径去重，每个视频独立生成任务。队列里的压缩任务可再次打开压缩配置；输出完成后可右键用天池播放器播放。

## 自动档与输出配置

默认选择“自动档（推荐）”，在高 / 中 / 低三个档位间切换。每档同时设置质量、最长边、帧率上限、音频码率与编码速度，不只改变一个百分比。格式、H.264 / HEVC、保留声音及 GPU 偏好仍可选择。

| 自动档 | 质量值 | 画面与帧率 | AAC 音频 | 编码速度 |
|---|---:|---|---:|---|
| 高 · 画质优先 | 20 | 保持原尺寸与帧率 | 192 kbps | 慢 |
| 中 · 均衡（默认） | 23 | 最长边 1920、不超过 30 fps | 128 kbps | 中 |
| 低 · 体积优先 | 28 | 最长边 1280、不超过 30 fps | 96 kbps | 快 |

这些是本项目的起点方案，画质不高于源文件；同一质量值在不同编码器间不代表相同视觉质量，体积优先也不保证结果一定小于已高度压缩的原文件。自动档使用画质控制，列表明确显示“体积由内容决定”，不伪造大小预估。

五种互斥的模式为：自动档、手动质量、手动码率、原体积百分比、目标 MB。手动质量为 1–51，数值越小通常画质越高、体积越大，提供高 20 / 中 23 / 低 28 快捷值；不把质量值当作画质评分或无损开关。手动视频平均码率为 64–200000 kbps，可选常用的 500 / 1000 / 2000 / 4000 / 8000 / 12000 / 20000 kbps，也可输入整数；1000 kbps = 1 Mbps，音频另计。

“更多输出配置”默认折叠，集中放格式、编码、分辨率、帧率、声音、编码速度和 GPU 选项。自动档的尺寸、帧率、音频码率及速度只读，切换手动模式即可独立调整；保留切换前的控件值。慢速档通常改善压缩效率，其具体能力依编码器而异，OpenH264 使用默认速度。

## 体积目标与批量处理

- 按原体积百分比：5–95%，默认 60%，即目标为原文件的 60%；快捷档为轻量 80%、均衡 60%、更小 40%。
- 按目标 MB：为每个视频设置同一个目标体积，MB 按 1,000,000 字节计算。目标必须小于各自的原文件；已有很小的视频应移除或改用百分比。
- MP4 / MOV / M4V / MKV，H.264 / HEVC；默认 MP4、H.264。软件回退缺少所选编码器时显示原因，可选择 H.264 或安装对应 FFmpeg 编码器。
- 分辨率保持原值或限制最长边为 1920 / 1280 / 854。保留比例并向下取偶数尺寸，不放大源视频。
- 保持原帧率或限制为 30 / 24 fps，不提高已知源帧率；源帧率无法确定时保持原值。
- 可保留或移除声音；保留时 AAC 使用 64 / 96 / 128 / 192 kbps，默认 128。只导出首个音轨，多声道最多下混为双声道。字幕、附件和额外音轨不导出；画面剪辑和轨道编辑仍使用快速剪辑。

窗口显示每个文件的时长、尺寸、原体积与输出尺寸。质量模式显示生效质量，码率和体积模式显示预计体积与视频码率；批次汇总也遵循这一差别。手动码率预计输出不小于源文件时显示提醒，允许用户按明确码率导出。读取失败、无有效视频画面、体积目标过小或不小于原体积时，显示逐行原因并阻止整个批次入队。

## 苹果视频

支持 iPhone、iPad、Mac 常见的 MOV、M4V、MP4 输入，包括 H.264、HEVC / H.265（含 10 位）和 ProRes。由 FFmpeg 解码，不依赖 Windows 的付费 HEVC 扩展。ProRes 输入重新编码为所选 H.264 / HEVC，压缩输出不保留 ProRes、透明通道或其编辑母版特性。选 MP4 + H.264 便于跨设备播放；MOV 为 QuickTime 容器，M4V 使用 MP4 封装。HEVC 在这些 Apple 容器中统一写入 `hvc1`，并将索引前置（faststart）。FFmpeg 按源显示矩阵旋转画面，压缩结果清零旋转标签，避免竖屏重复旋转。

压缩输出采用 8 位 SDR。检测到 HLG（`arib-std-b67`）或 PQ（`smpte2084`）时，先通过 zscale 线性化为浮点 RGB，转换 BT.709 色域、用 mobius 做色调映射，最后抖动量化为 8 位并明确写入 BT.709 / limited-range 标记；不能直接把 HDR 像素当 SDR 编码。iPhone Dolby Vision Profile 8、兼容标识 4 使用标准 HLG 基础层，缺失 transfer 标记时从配置记录识别。压缩后删除旧 HDR / Dolby Vision 帧侧数据，不声称保留或重建 Dolby Vision 动态元数据；逐文件列表和任务日志明确显示 HDR → SDR。

此路径需要 FFmpeg 的 `zscale`、`tonemap`、`sidedata` 滤镜，执行前读取能力清单，缺少时显示具体原因。macOS 配方固定 zimg 3.0.6 并启用 `--enable-libzimg`；Windows 可替换引擎须包含这些滤镜。Dolby Vision Profile 5 的非标准色彩基础层及无法识别的 Dolby Vision 基础层不会按普通 HDR 导出；HDR 保留输出、Apple Log LUT 和受 DRM 保护的视频未实现。

依据 [Apple 的 HEVC 格式说明](https://support.apple.com/en-us/116944)、[Apple 的 iPhone HDR / Dolby Vision 技术文档](https://developer.apple.com/av-foundation/Incorporating-HDR-video-with-Dolby-Vision-into-your-apps.pdf) 与 [FFmpeg zscale / tonemap 文档](https://ffmpeg.org/ffmpeg-filters.html#tonemap)。本次检查仅为受影响项目编译、滤镜能力静态清单和构建配置语法，尚未运行苹果源视频的实际编码验证。

## 编码与结果

码率 / 体积模式用目标字节数和源时长计算预算，预留 3% 容器空间并扣除音频，剩余为视频平均码率。手动码率直接使用输入的 kbps。单次平均码率路径设置码率及峰值 / 缓冲限制，不混入 CRF、CQ 或 CQP；目标 MB 不是精确大小保证，暂不提供双遍编码。

质量模式独立映射到编码器的画质控制：x264 / x265 使用 CRF；NVENC 使用 CQ；QSV、AMF 使用量化控制；Apple Silicon VideoToolbox 使用质量参数；OpenH264 使用质量模式并固定量化范围；Kvazaar 使用 QP。此模式不附加目标体积、平均码率或峰值限制。CPU / GPU 之间只保留相同的质量意图，不能保证视觉结果一致。

“优先 GPU 编码”遵循全局自动 GPU 设置。只选择用户指定的 H.264 / HEVC 编码家族，复用已有 NVENC、QSV、AMF、VideoToolbox 检测、硬件解码与失败回退；取消 GPU 优先时直接软件编码。软件 H.264 优先 libx264，其次 libopenh264；HEVC 优先 libx265，其次 libkvazaar。码率 / 体积模式的硬件和软件路径使用同一个预算，质量模式保留相同质量意图。

仅有 Kvazaar 可用于 HEVC 软件回退时，输出宽高须为 8 的倍数；不满足时提示改用 H.264 或可用 GPU，不擅自拉伸或增加黑边。该约束来自 [FFmpeg Kvazaar 封装](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/libkvazaar.c)。

预计体积是预算估算，不保证精确大小；短视频容器开销、内容复杂度和码率控制会影响实际结果。任务完成后按实际大小显示节省百分比，日志记录原体积、所选质量 / 码率 / 体积目标、实际体积和编码器。输出没有缩小时明确显示“输出未缩小”。源文件不会被覆盖，沿用输出目录、源目录和设置名称偏好。

交互参考开源项目 [HandBrake 的质量与码率模式](https://handbrake.fr/docs/en/latest/technical/video-cq-vs-abr.html)、[HandBrake 画质调整](https://handbrake.fr/docs/en/latest/workflow/adjust-quality.html) 和 [Shutter Encoder 的码率、质量、目标体积与速度设置](https://www.shutterencoder.com/documentation/)。借鉴预设优先、质量 / 码率分开、高级参数折叠的操作方式；三档具体参数由本项目定义。

码率参数参考 FFmpeg 官方实现：[NVENC](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc_h264.c)、[AMF](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/amfenc_h264.c)、[OpenH264](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/libopenh264enc.c)。

本次按仓库约定检查受影响项目编译和相关静态诊断，未运行实际媒体压缩、界面截图或性能回归。预估精度与各硬件平台的实际编码效果尚未实测。
