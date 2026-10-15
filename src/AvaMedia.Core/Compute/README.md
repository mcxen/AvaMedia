# GPU 优先计算层（后端）

默认存储目录为 LocalApplicationData/AvaMedia：`compute-settings.json`、`compute-capabilities.json`、`compute-throughput.json`。不增加 NuGet 依赖。

- `ComputeSettings.Load(autoDetectGpu)`：独立设置存在时以它为准；不存在/损坏时，旧 AutoDetectGpu=false 推导 ForceCpu，否则 Auto。支持 Clone/CopyFrom/Save；以后可迁入 AppSettings。
- Auto/PreferGpu 均优先尝试硬件并保留 CPU 兜底；ForceCpu 不探测 GPU。显式 libx264 等软件编码器保持软件编码，但允许独立 GPU 解码。
- `ComputeRouter.Plan` 产生平台候选；调用方先读取探测结果，再实际初始化/执行；`ReportFailure` 推进候选并记录原因。不要把请求 GPU 当作实际执行成功。
- `ComputeCapabilities` 注入 probe、clock、files；成功 7 天、一般不可用 1 天、超时 1 小时、运行失败 10 分钟。超时先从 6 秒延长到 20 秒重测。运行期失败在本进程禁用，下一次进程启动按短 TTL 再测。`Invalidate()` 供未来重新检测入口使用。
- 指纹包含 OS、进程架构、机器名、工具路径/mtime、ORT 版本、驱动摘要。Fingerprint 接受注入的 files/driverDigest，方便无 GPU 测试。
- FFmpeg 编码顺序：Windows NVENC>QSV>AMF>CPU；Linux NVENC>VAAPI>QSV>AMF>CPU；macOS VideoToolbox>CPU。VAAPI 当前设备为 /dev/dri/renderD128。
- FFmpeg 解码顺序：Windows CUDA>D3D11VA>QSV>CPU；Linux CUDA>VAAPI>CPU；macOS VideoToolbox>CPU。压缩样片解码探测独立于硬件编码探测。下载为 nv12/p010le 后复用原有 CPU 滤镜。
- PersonClipSampling、PersonBoundaryFrames、ContactSheetFrames、VideoSummarySampling 使用共享解码执行器；失败或不完整的 GPU 输出整段丢弃，用软件重跑。rawvideo 暂存于自动删除的临时文件，验证进程成功后才交付帧，避免重复交付；这会增加临时磁盘占用与首帧等待。缩略图也已覆盖 Linux CUDA/VAAPI。
- ONNX：Windows DirectML>CUDA>CPU；Linux CUDA>ROCm>OpenVINO GPU>CPU；macOS CoreML>CPU。仅尝试 GetAvailableProviders 报告存在的 EP，先验证极小 Identity 模型，再加载真实模型。CoreML 保留 MLProgram→NeuralNetwork 重试；运行失败可继续 CPU。原生 ORT 建会话没有可安全中止的托管取消 API，因此该初始化探测不能由 6/20 秒参数硬终止；进程型 FFmpeg/llama 探测有硬超时。
- Whisper：Windows/Linux Vulkan>CUDA/CUDA12>CPU/CpuNoAvx；macOS 默认运行库内含 Metal（1.9.1 没有 RuntimeLibrary.Metal），通过日志确认使用。实际 Backend 按 LoadedLibrary、UseGpu 和 Metal 记录生成；已加载运行库不能热切换，ForceCpu 仍强制 UseGpu=false。Silero 在 macOS 保留 CPU 策略。运行库顺序与创建会话串行化，避免并发任务覆盖首次加载配置。
- llama：先 --list-devices；无 GPU 或 ForceCpu 使用 --device none；日志未确认卸载任何层时按 CPU 报告，0 层卸载给出原因。GPU 启动或推理失败继续 CPU。
- `ThroughputModel` 用 tool/backend/resolution/codec 分键，EWMA 持久化；`PriorAwareProgressEstimator` 初始使用保守先验，15 秒内混合实测；新尝试或回退重置速度窗口。`ComputeProgressScope` 在编码期间维护已有 Job.Estimate，抵御队列计时器写回旧估计；退出即释放。
- `ComputeJobContext` 通过 AsyncLocal 将实际推理后端关联到任务，缓存会话执行时也会报告。`Job.ComputeOutcome` 由新 partial 暴露，数据实际放在 ConditionalWeakTable，并用任务文件日志中的 `[compute-outcome]` 单行 JSON 恢复。队列 JSON 与多阶段契约不变。`ComputeRuntimeFormatter.Backend/Note` 可直接用于今后的 TaskRuntimeInfo 字符串。

## 代码事实与未接入范围

- 当前 `video-slim` 已拆到 VideoSlimming.Execution.cs，采样结果/Matches 校验绑定 libx264/libx265 medium CRF；不能复用此结论直接切换 GPU 编码。本次保留该工具的 CPU 质量流程，在 MediaEngine 外围增加明确原因、吞吐和 ETA。硬件编码需要后续开放这些文件并实现对应的质量重新校准。`video-compress` 和普通转码已接入 GPU 编码。
- 受保护的 VideoOrientationDetector 仍使用原有软件抽帧，待接入 FrameDecodeArguments。未改任何受保护的服务、多阶段契约、Desktop/Browser/XAML/tools。
- PreserveSourceAttributes 继续服从现有 SourceVideoGpu 支持矩阵；VAAPI 尚未接入该特定导出链路。HDR 色调映射和其余现有软件滤镜继续使用 CPU。
- ORT 会话可以混合 EP 与 CPU 节点；“DirectML / CPU”等标签表示会话启用此 EP，不能保证图上全部算子在 GPU 执行。Whisper 加载 Vulkan/CUDA 运行库也不能证明每个算子都在 GPU。
- UI 后续：设置偏好、重新检测、任务 Backend/Note 与回退链展示；独立设置迁入 AppSettings 的 Clone/CopyFrom/持久化。Job 目前已有属性入口，日后可把附加存储并入正式任务持久化字段。

## 验证与打包

仅构建 Core 和 AvaMedia.ComputeTests，运行新增控制台测试。全部进程、EP 初始化、时钟与存储相关测试均使用 fake，不要求 GPU，不运行媒体回归或截图。

真机需验证 NVENC/VAAPI/QSV/AMF/VideoToolbox 编解码、10bit 下载、VAAPI 上传与复杂滤镜、设备权限和非 renderD128 场景；各 EP 的真实模型支持；Whisper Vulkan 与 macOS Metal 日志、首次加载并发；llama 卸载层数/显存不足回退；真实吞吐先验校准。

Linux 当前包仍为 CPU ORT，代码支持不会自动产生 CUDA/ROCm/OpenVINO EP。建议加速依赖按需下载，按平台/架构匹配本地 ORT 与 managed 版本；CUDA/cuDNN 独立加速包，避免增加主包体积。Linux FFmpeg 需启用 VAAPI/NVENC，并具有 /dev/dri 权限和对应驱动；arm64 Whisper 当前未带 Vulkan 包。未改依赖或打包脚本。
