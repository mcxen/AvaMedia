# 实际视频服装分析与资源策略验收

2026-10-11，在 Apple M4（10 CPU 核、8 GPU 核、16 GB 内存）、macOS 15.7.1 上，抽取用户原始视频批次中的 6 个视频，每个采样 8 帧。运行生产 `QueueService → MediaEngine → FolderClassificationJobService`，保留逐文件检查点、服装分组与整理预览。

## 分类结果

三次完整分析均完成 6 个视频、48 个采样帧，输出相同的 3 个服装组，0 个待确认、0 个分析失败。原始视频的 SHA-256、大小和修改时间保持不变。

| 分组 | 文件 |
| --- | --- |
| 01 | IMG_9455.MP4、IMG_9457.MP4、IMG_9458.MP4 |
| 02 | IMG_7846.MP4、IMG_9454.MP4 |
| 03 | 01D81AFD-F90C-44C6-8199-B9F6828F5060.MOV |

这是算法分组输出和生产流程验收；没有建立人工服装身份标注，不能据此给出同一件衣服的准确率，也没有覆盖原批次全部 169 个视频。

## 实测发现与修复

1. DINOv2 的 `num_channels`、`height`、`width` 仍为动态维度。Core ML 原先只接管 643 个节点中的 1 个；明确实际使用的 3×224×224 输入后，接管优化图全部 650 个节点。
2. MediaPipe 服装模型的 Core ML MLProgram 编译报 `Required param 'pad' is missing`，原先直接回退 CPU。现在先重试 Core ML NeuralNetwork 格式，成功接管 220 个节点中的 207 个，13 个辅助节点仍在 CPU。

修复后的 6 视频复核重新计算全部服装采样帧，沿用已完成的真实 JoyTag 结果，实际覆盖生产“已有标签、补齐服装特征、重新分组”的恢复路径。此前完整冷启动运行已对这 6 个视频生成 Core ML JoyTag 结果。

最终后端为 `MediaPipe: Core ML (NeuralNetwork) / CPU · DINOv2: Core ML / CPU`，无 CPU 回退原因。ONNX 推理记录包含 MediaPipe 的 528 次 Core ML 分区调用与 624 次 CPU 辅助节点调用，以及 DINOv2 的 36 次 Core ML 整图调用；部分帧服装区域不足，按生产策略跳过特征编码。

使用相同编译模型与 `ALL` 配置读取 Apple `MLComputePlan`：DINOv2 的 650 个操作优先分配到 GPU；MediaPipe 的 11 个 NeuralNetwork 分区共 195 个层优先分配到神经网络引擎。结合实际 Core ML 推理记录，可以确认本机硬件加速路径生效，不能将整机 GPU 占用率单独当作本任务加速证据。[Core ML 配置资料](https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html)。

## CPU 预留

CPU 对照仅在隔离源码快照中禁用加速 provider，产品仍保持 GPU 优先。配置为多线程开启、每任务线程上限 8、并行任务数 2。

| 预留比例 | 原生 ONNX 线程上限 | 分析期间本进程平均 / 最大 CPU | 分组 |
| --- | --- | --- | --- |
| 0% | 4 | 30.5% / 38.6% | 3 组，6 个视频 |
| 25% | 3 | 24.6% / 29.8% | 相同分组 |

CPU 百分比按整机 10 核归一化。25% 对应工作线程预算 7，按 2 个并行任务分配后每模型上限 3；原生会话日志确认实际线程设置与关闭等待空转的配置。

真实视频并行分析造成的整机高负载期间，记录到 17 次 CPU 退让，每次 25–160 ms，触发时整机 CPU 为 77.2–95.1%。另用同一实际视频的 7 个新采样时间检查未缓存的视频解码，7 个 FFmpeg 子进程全部为 `BelowNormal`，实际参数为解码 2 线程、编码 3 线程、滤镜 1 线程。该次视频分析完成，原文件哈希不变。

这证明线程预算、计算进程优先级和高负载退让已生效；它们不是 OS 硬占用率配额，不能保证整机固定有 25% 空闲。

## 验证边界与证据

本轮各阶段的模型加载、预览缓存和其他应用负载不同，不是条件一致的速度基准。GPU 路径虽然实际运行，但本轮没有体现整体速度优势，不能宣称 GPU 比 CPU 更快。CPU 采样数值作为本次运行记录，不单独用于推导预留策略的精确降幅。

测试机为 M4，没有验证截图中的 Intel UHD、Windows DirectML、发布安装包或原生窗口交互。相关 Desktop Release 编译通过，0 警告、0 错误。

本机证据位于忽略目录 `artifacts/outfit-video-gpu-cpu-20261011/`：`acceptance-summary.json`、各运行的 `acceptance.json` / `results.json` / `resources.jsonl` / `profiles/`、两个 `*-compute-plan.json`、原文件指纹、源码快照与构建日志。诊断快照增加 profiling、CPU 对照开关和日志；这些诊断开关未进入产品。

首轮额外开启的 Core ML compute-plan 加载诊断被中止并留存堆栈；后续改为分析完成后单独读取计划。一次完成分析后的文件指纹检查遇到并发文件锁，已独立重试并核对全部原文件哈希；最终复核完成完整指纹检查。
