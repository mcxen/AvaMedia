# 自动 GPU 视频转码

普通视频输出的编码器保持默认「自动」、高级设置保持「自动适配 GPU」开启即可。任务按当前平台、实际可用的编码器、输出容器和源视频编码选择后端，无需指定显卡品牌或填写 FFmpeg 参数。流复制和明确指定的软件编码器按原选择执行。「原格式 / 原属性」旋转、裁剪也自动选择同编码的 GPU 实现，目前接受 8 位和 10 位 4:2:0 源；保留位深、色度采样、色彩与复制轨道，失败时重新选择原编码的软件实现，见 [原属性 GPU 导出](SOURCE-VIDEO-EXPORT.md)。

Linux 另支持 VAAPI：FFmpeg 编码候选顺序为 NVENC → VAAPI → QSV → AMF → CPU，解码为 CUDA → VAAPI → CPU，当前设备为 `/dev/dri/renderD128`。需要 FFmpeg 构建启用 VAAPI 并具有 `/dev/dri` 权限和对应驱动；具体芯片支持的子集仍由运行时探测决定。Linux 发布包中的 ONNX Runtime 仍为 CPU 版本，代码支持不会自动产生 CUDA / ROCm / OpenVINO 执行提供者。

除转码外，抽帧、缩略图、ONNX 推理、Whisper 与 llama 也按同一套路由层选择实际执行后端，细节见 [GPU 优先计算层](../src/AvaMedia.Core/Compute/README.md)。

## 后端接口

`IHardwareTranscodingBackend` 封装平台范围、编码器清单、硬件设备初始化、质量参数和输入解码计划。`HardwareTranscoding` 维护内置后端；`HardwareAcceleration` 负责能力发现和候选排序；`MediaEngine` 执行选择、重试、临时文件交付和任务日志。

| 后端 | 平台 | 已接入的硬件编码 |
| --- | --- | --- |
| Apple VideoToolbox | macOS，面向 Apple Silicon 发布 | H.264、HEVC |
| NVIDIA NVENC | Windows / Linux | H.264、HEVC、AV1 |
| Intel Quick Sync | Windows / Linux | H.264、HEVC、AV1、VP9 |
| AMD AMF | Windows / Linux，依赖 FFmpeg 构建与驱动 | H.264、HEVC、AV1 |
| VAAPI | Linux，依赖 FFmpeg 构建与驱动 | H.264、HEVC 及 FFmpeg 该构建支持的其余编码器 |

具体芯片支持的子集由运行时探测决定，不把品牌或编码器存在视为可用。Apple 后端不声明 AV1 编码；AV1 解码仍取决于芯片和系统。Intel 不要求用户区分核显与 Arc：硬件实现可用时使用 QSV。

## 自动选择

1. 按平台筛选：macOS 使用 VideoToolbox；Windows 使用 NVENC、AMF、QSV；Linux 使用 NVENC、VAAPI、QSV、AMF。
2. 读取配置的 FFmpeg 编码器列表，只对存在的编码器编码 3 帧测试画面；探测使用与正式输出相同的硬件初始化、像素格式和质量策略。
3. 结果按 FFmpeg 路径与修改时间缓存；「查看 GPU 能力」可主动刷新。探测属于客户端运行时能力发现，不是开发验证测试。
4. 在容器允许的范围内优先匹配源视频编码家族；其余候选按 H.264、HEVC、AV1、VP9 排序，同家族按 NVENC、AMF、QSV 排序。macOS 只涉及 Apple 后端。

MP4 / MOV / M4V 接入 H.264、HEVC、AV1；TS / MTS / M2TS 接入 H.264、HEVC；MKV 接入全部四种；WebM 接入 AV1、VP9；AVI / FLV 接入 H.264。MP4 / MOV / M4V 的 HEVC 使用 `hvc1` 标记。编码器可封装不代表所有第三方播放器都支持该编码。

## 与计算路由层的关系

转码候选来自上表的转码后端表；抽帧、缩略图、ONNX、Whisper 与 llama 的后端顺序来自共享计算路由层（`ComputeRouter`）。两者的探测结果都按指纹与 TTL 缓存，请求某后端不等于实际执行成功，实际后端与回退链由任务日志中的执行结果记录。转码与本机推理使用同一偏好：默认 Auto 优先尝试硬件并保留 CPU 兜底，ForceCpu 不探测 GPU，显式指定的软件编码器保持软件编码。

## 解码与滤镜

NVENC 请求 CUDA / NVDEC 解码，QSV 请求对应的 QSV 解码器，VideoToolbox 请求系统硬件解码，Windows AMF 请求 D3D11VA 解码。VAAPI 在 Linux 请求对应的 VAAPI 解码器；AMD Linux 当前只接入硬件编码。

单输入、无视频滤镜、无旋转元数据的 8 位 4:2:0 视频，NVENC 和 QSV 使用硬件画面直接输入硬件编码器。其他视频编辑、合并、字幕烧录等沿用已有滤镜：硬件解码的画面下载到系统内存后处理，再送入硬件编码。音频处理仍走现有路径。

解码参数应用于实际选中的视频轨，并只写入对应输入的 `-i` 之前。QSV 初始化 `hw_any` 硬件设备；编码与解码共享同一命名设备，避免误用软件 QSV 实现。高位深源下载使用 P010；现有普通输出继续采用 8 位 NV12，未新增 HDR 保留承诺。

## 质量与回退

- Apple Silicon：VideoToolbox 常量质量刻度与客户端低值高质量刻度反向换算，并指定 `allow_sw=0`，普通转换使用非实时模式。FFmpeg 的常量质量模式仅支持 Apple Silicon；旧 Intel macOS 路径使用按尺寸、帧率与质量估算的码率。
- NVIDIA：VBR + CQ；普通转换使用质量预设；AV1 与 H.264 / HEVC 使用各自的质量范围。
- Intel：CQP，通过 `-q:v` 正确设置量化单位；AV1 换算到 0–255 量化范围。不同硬件的同一数字不保证相同画质或文件大小。
- AMD：CQP + I/P 帧量化值；AV1 使用相应的 0–255 范围；普通转换使用均衡预设。

自动任务先尝试硬件解码与编码。该链路失败时保留硬件编码器、改用软件解码重试；仍失败则尝试其他已探测可用、兼容容器的编码器，最后回退软件编码。某种解码方法失败后，本任务不重复尝试该方法。用户明确指定的硬件编码器可回退软件解码，但保留其编码选择，失败反馈原因。

硬件尝试写入输出目录的唯一临时文件，成功后移动到目标输出。失败和取消清理本次临时文件；任务选项及源文件不被改写。日志保留硬件选择、解码请求和回退原因，不把解码请求写成已验证的硬件执行结果。

## 本次开发验证

按 AGENTS.md 仅执行相关客户端编译、XAML 编译及静态检查；未运行功能回归、媒体输出或性能测试。Apple Silicon、NVIDIA、Intel、AMD 与 VAAPI 各设备的实际转码性能与驱动兼容性尚未逐机验收；Linux 包当前仍为 CPU ORT。

实现依据：[FFmpeg 硬件设备与解码选项](https://ffmpeg.org/ffmpeg.html)、[NVIDIA 官方 FFmpeg 接入](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/ffmpeg-with-nvidia-gpu/index.html)、[FFmpeg QSV 编码源码](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/libavcodec/qsvenc.c)、[VideoToolbox 编码源码](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/libavcodec/videotoolboxenc.c)、[AMD AV1 编码源码](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/libavcodec/amfenc_av1.c)。
