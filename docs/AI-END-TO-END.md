# AI Beta 端到端验证

2026-10-08，在 macOS 15.7.1、M4 MacBook Air、16 GB 内存上完成本机验证。范围是图片／视频标签、语义重命名、词库、进度和有人片段导出。没有执行全量回归或 Windows 硬件验证。

## 已修复

- 标签窗口沿用全局 `AutoDetectGpu` 设置，提供 Core ML 调度和首次编译的提示。
- GPU 提供程序不可用或推理回退时记录原因；标签结果和 JSON 导出保留原因。
- 模型安装校验使用打开文件后的内容长度。macOS 推理工具含 dylib 符号链接，之前 `FileInfo.Length` 与清单中实际内容长度不同，导致下载完成后仍显示未安装。
- Gemma 保存有限的设备／GPU 层分派信息，并在语义处理进度中显示实际观察到的后端。只有运行时报告非零 GPU 层时才显示 Metal；普通自动调度状态不作为 GPU 已执行的证明。

## 实际结果

使用此前 `/Users/mcx/Downloads/pikpak` 的本地测试清单，选取 I01、I03、I08、I12 四张图片和 V01、V02 两个视频。分析、改名和视频导出均使用副本；原始文件通过 SHA-256 检查保持一致。

| 验证 | 结果 |
| --- | --- |
| 内置词库 | 全部通过字段和标签校验，JoyTag 5813 个标签 |
| 自定义词库 | 2000 个词保存、重新读取、独立选择配置、TSV／JSON 往返及来源信息通过 |
| 原生词库界面 | 新建 2000 词词库成功；候选搜索、跨搜索选择和使用所选词通过 |
| Beta | 默认隐藏；选项开启后主界面入口和词库页出现 |
| JoyTag | CPU 与 Core ML 均完成六个文件；分数最大差异小于 0.01；每视频最多八帧 |
| 标签界面 | 实际文件／帧进度、中间标签和画面预览可见；分析后可预览、执行改名并撤销 |
| 取消 | 保留已经回调的结果；释放模型使用锁，能够再次获取模型 |
| 语义模型安装 | 官方固定文件下载成功；模型、推理工具及符号链接对应内容的完整哈希校验通过 |
| Metal | 运行时识别 Apple M4，报告 `offloaded 25/25 layers to GPU`，无启动回退 |
| 语义匹配 | 34 个候选词（含 500 字符描述）多请求编码和四帧图像向量通过；取消及模型锁释放通过 |
| 原生语义界面 | V01／V02 在默认阈值下匹配到“有人”，相似度分别约 0.675／0.655；显示 Metal 后端和中间画面；两个视频真实改名并撤销成功 |
| 有人片段导出 | V01 采样 47 帧、实际推理 46 帧，生成两个区间；合并导出 16.64 秒，区间总长 16.625 秒；完整视频解码通过 |

Core ML 会话创建和推理成功不代表所有算子都使用 GPU 或 Neural Engine。本次没有逐算子确认 ANE 分派。人物区间没有人工逐帧标注，语义分数也没有进行准确率／内容分级校准。

六文件整段流程记录为 CPU 21.49 秒、Core ML 151.05 秒，包含加载、可能的编译、抽帧和推理；同时还存在模型下载等本机活动，因此不是独立推理性能基准。不能据此承诺开启 GPU 一定更快。[Core ML 缓存及计算计划说明](https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html)解释了编译开销与实际硬件分派的诊断方式。

原生界面测试使用独立的主窗口配置、媒体副本和临时应用。结束后移除了本次创建的词库，恢复原先候选选择和改名记录；下载好的可选语义模型保留在模型管理中。

## 重复验证

测试工具在 `tests/AvaMedia.AiTests`，只在用户要求相应验证时运行。清单是 JSON 对象，`files` 数组包含 `id` 和本地 `path`；此次使用的六个 ID 如上。清单及原始媒体不进入 Git。

```sh
.tools/dotnet/dotnet build tests/AvaMedia.AiTests/AvaMedia.AiTests.csproj -c Release -r osx-arm64 --artifacts-path artifacts/ai-e2e-final-build
.tools/dotnet/dotnet artifacts/ai-e2e-final-build/bin/AvaMedia.AiTests/release_osx-arm64/AvaMedia.AiTests.dll core artifacts/ai-e2e /path/to/inventory.json
.tools/dotnet/dotnet artifacts/ai-e2e-final-build/bin/AvaMedia.AiTests/release_osx-arm64/AvaMedia.AiTests.dll download artifacts/ai-e2e
.tools/dotnet/dotnet artifacts/ai-e2e-final-build/bin/AvaMedia.AiTests/release_osx-arm64/AvaMedia.AiTests.dll semantic artifacts/ai-e2e /path/to/inventory.json
```

`download` 会显式下载可选语义模型；`core` 和 `semantic` 只使用已经安装的模型。`ui` 模式启动真实 Avalonia 主窗口，其主窗口设置保存到输出根目录下的 `ui-state`。模型、词库和改名记录仍使用应用数据目录，进行原生操作验证时须备份并在完成后恢复测试影响。

本机匿名记录保存在 `artifacts/ai-e2e/core.json`、`semantic.json`、`ui.json`，导出视频在该目录的 `output` 子目录。最后受影响 Desktop／测试项目的 Release ARM64 构建通过，0 警告、0 错误。
