# 本地模型加速

所有实际加载的本地 AI 模型统一先尝试平台加速后端。后端不可用、初始化失败或推理失败时才回退 CPU；不再通过首次测速主动选择 CPU。分析、模型缓存、后台预热和队列任务采用同一策略，AI 参数及窗口中的 GPU 开关已移除。视频编码的硬件加速选项独立保留。

## 全量清单

清单覆盖 `ModelCatalog`、`Assets/SummaryModels.json` 和三个内置模型；llama.cpp 运行库是执行工具，单独列出。

| 模型 / 文件 | 推理入口 | Windows | macOS | CPU 回退 / 现状 |
| --- | --- | --- | --- | --- |
| MediaPipe clothes | `OutfitAppearanceService` → `ModelInferenceSession` | DirectML | Core ML | 初始化或执行失败；视频采样帧也使用同一路径 |
| DINOv2 small | `OutfitAppearanceService` → `ModelInferenceSession` | DirectML | Core ML | 初始化或执行失败；与服装分割模型分别回退 |
| JoyTag | `MediaTagModelCache` → `ModelInferenceSession` | DirectML | Core ML | 初始化或执行失败 |
| Marqo NSFW | `RealNsfwClassifier` → `ModelInferenceSession` | DirectML | Core ML | 初始化或执行失败 |
| YOLOX | `PersonDetectorSet` → `ModelInferenceSession` | DirectML | Core ML | 初始化或执行失败 |
| NanoDet | `PersonDetectorSet` → `ModelInferenceSession` | DirectML | Core ML | 初始化或执行失败；不再执行 CPU/GPU 速度比较 |
| MediaPipe person | `PersonDetectorSet` → `ModelInferenceSession` | DirectML | Core ML | 初始化或执行失败 |
| EmbeddingGemma 2 文本 q4 | `GemmaMediaEmbedding` | DirectML | Core ML | 初始化或执行失败；量化算子可能由 CPU 执行 |
| EmbeddingGemma 2 视觉编码器 q4 | `GemmaMediaEmbedding` | DirectML | Core ML | 初始化或执行失败；与文本模型一起回退 |
| Whisper Tiny q5_1 | `SpeechInferenceSession` | x64 Vulkan | Metal | 原生后端选择可用设备；加载或推理失败时重建 CPU 上下文 |
| Whisper Base q5_1 | `SpeechInferenceSession` | x64 Vulkan | Metal | 同上 |
| Whisper Small q5_1 | `SpeechInferenceSession` | x64 Vulkan | Metal | 同上 |
| Qwen3.5 0.8B abliterated Q8 文本 | `LocalSummaryModelCache` → `LocalSummaryModel` | Vulkan | Metal | 启动或首次有效输入执行失败时 CPU 重试 |
| SmolVLM2 500M Q8 + mmproj Q8 | `LocalSummaryModelCache` → `LocalSummaryModel` | Vulkan | Metal | 同上；视觉投影也启用 GPU offload |
| Qwen3.5 4B abliterated Q4 + mmproj f16 | `LocalSummaryModelCache` → `LocalSummaryModel` | Vulkan | Metal | 同上；视觉投影也启用 GPU offload |
| YuNet（内置） | `VideoOrientationDetector` → `ModelInferenceSession` | DirectML | Core ML | 为 320 / 640 采样分别固定动态尺寸，缓存按尺寸隔离；保持 CPU 回退和取消支持 |
| Silero VAD v6.2（内置） | `SpeechInferenceSession` | x64 Vulkan | Metal | GPU 优先；加载或语音检测失败时 CPU 重试 |
| RNNoise `sh.rnnn`（内置） | FFmpeg `arnndn` | CPU | CPU | 当前滤镜没有 GPU 执行入口 |
| LaMa | `LaMaModelInstaller` | 尚无推理入口 | 尚无推理入口 | 目前仅下载、校验和管理模型，不宣称已支持 GPU 推理 |
| llama.cpp b11476 运行库 | `LocalSummaryModel` | Vulkan 包 | Metal 包 | `--gpu-layers auto`；CPU 重试使用 `--gpu-layers 0 --device none` |

Linux 目前打包的 ONNX Runtime 没有启用 GPU provider，ONNX 模型会记录不可用原因后使用 CPU；llama.cpp 使用现有 Vulkan 运行库，Whisper 在 Linux x64 也携带 Vulkan 包。Windows ARM64 的 ONNX 模型使用 DirectML，但 Whisper.net 1.9.1 没有 ARM64 Vulkan 包，因此语音模型仍使用可用的原生 CPU 后端。

## 后端与回退记录

ONNX 标签、语义和人物结果保留后端及回退原因。服装分析同时保存 `OutfitBackend`、`OutfitFallbackReason`，并在任务进度和日志中记录两个模型的后端及回退。YuNet 返回结果新增同样的字段。字幕任务显示原生运行库及 GPU 优先策略，加载 / 执行失败的显式 CPU 重试写入日志；GPU 优先不是对每个算子实际执行设备的断言。

DirectML 可用于支持 DirectX 12 的 Intel、AMD 和 NVIDIA GPU。Core ML 的 `ALL` 允许 GPU、神经网络引擎和 CPU；ONNX provider 可以将不支持的节点留给 CPU，故后端名称保留 `/ CPU`。核显仍共享系统内存；本次改动保证优先级，不保证每一种硬件、算子和输入都比 CPU 快。

Whisper Windows x64 同时携带 `Whisper.net.Runtime.Vulkan` 和原有 CPU 包，使用库的默认 GPU 优先加载顺序。macOS 原有 `Whisper.net.Runtime` 已携带 Metal 后端，不需要额外的 Core ML 编码器权重。

资料：[ONNX Runtime DirectML](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)、[Whisper.net 运行库](https://github.com/sandrohanea/whisper.net)。本次仅进行相关项目编译和静态检查，没有执行模型推理、媒体验收或性能测试。

## CPU 余量

选项 → 转换 → 任务与线程中的“CPU 预留比例（%）”默认 25，可调 0–80；0 关闭余量控制。GPU 优先策略保持不变，CPU 预算也覆盖 GPU 模型的预处理及 CPU 节点。

开启后，计算任务共用按逻辑处理器数量折算的工作线程预算。每任务线程上限同时受手动线程数、并行任务数和预留比例限制；跨工具计算并发也受预算限制，下载仍使用独立队列。FFmpeg 解码最多 2 线程、滤镜 1 线程，编码线程服从预算及较低的手动限制；Whisper / VAD、ONNX 和 llama.cpp 的 CPU 线程采用同一预算。

模型线程关闭等待空转；FFmpeg 任务与 llama.cpp 子进程降低调度优先级。整机 CPU 超过目标占用时，在任务安全检查点、CPU 推理和语音分块之间短暂退让。设置保存后应用于后续调度和处理单元；原生模型缓存按 CPU 配置重建，正在执行的推理不强制中断。

这是应用的资源预算和调度提示，不能保证整机固定有 25% 空闲：原生库额外线程、正在执行的计算和其他应用仍会使实际占用波动。没有执行性能或响应性测试。等待空转参数依据 [ONNX Runtime 线程配置](https://onnxruntime.ai/docs/performance/tune-performance/threading.html) 与 [llama.cpp b11476 参数](https://github.com/ggml-org/llama.cpp/blob/b11476/common/arg.cpp)。
