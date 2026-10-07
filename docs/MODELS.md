# 模型管理与自动人物剪辑 Beta

选项 → 常规 → 开启 Beta 功能，应用后在视频分类中显示“保留有人片段 · Beta”。默认关闭。关闭 Beta 会隐藏入口，已有普通剪辑与合并任务继续使用现有队列。

选项 → 模型管理提供下载、取消、继续下载、SHA-256 校验和删除。模型保存在用户数据目录 `AvaMedia/models/`，按模型分目录；下载写入独立暂存目录，校验完成后发布。分析持有模型使用锁，下载或使用期间不能删除同一模型。YuNet 为内置资源，只展示状态。

| 模型 | 用途 | 下载方式 |
| --- | --- | --- |
| YOLOX | 检测人物，自动剪辑必需 | 手动下载，约 34.2 MiB |
| EmbeddingGemma 2 Q8 | 可选本地语义辅助 | 手动下载，权重与投影约 824.6 MiB，另含匹配平台的 llama.cpp 工具 |
| LaMa | 图片修复 | 保留启动自动下载选项，可关闭后删除 |

Google 模型下载完成后不会自动启用。自动剪辑窗口中可勾选“使用 EmbeddingGemma 2 语义辅助”。未下载时此选项不可用，YOLOX 可独立完成分析。视频不上传到远程服务；语义辅助使用仅绑定 `127.0.0.1` 的临时本地进程，带随机访问密钥，结束或取消时关闭。

分析默认每秒采样 2 帧，以人体检测分数生成有人区间；对有人／无人变化边界进一步采样，细化至约 0.1 秒。默认前后各保留 0.5 秒、合并短间隔，并保留弱检测证据。可修改采样频率、阈值、前后余量、合并间隔、最短片段时长和不确定片段规则。语义辅助比较人物与空场景文本的嵌入相似度，辅助保留人体检测较弱的画面；相似度差值不是概率。

分析结果进入现有多片段编辑器，可以修改起止时间、删除和调整片段。输出可分别保存，或把每个源视频的保留片段合成一个文件。合并使用现有重新编码流程，最多 64 个片段；Fast Copy 只用于分别导出，边界受关键帧限制。没有可保留片段的源视频不生成任务；取消或分析失败不改动任务队列。

Beta 的采样可能漏掉极短出现的人物，小人物、遮挡、低光和画面中的人物海报也可能造成误判。当前交付范围为源码编译和必要静态检查，未运行真实视频效果、性能或模型推理回归。

来源与固定版本：

- [OpenCV YOLOX](https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox)，Apache-2.0；ONNX 权重使用 OpenCV Hugging Face 修订 `d4938dfc9d4ec5d098bfa33e98b3f3345a236586`。
- [EmbeddingGemma 2 模型卡](https://ai.google.dev/gemma/docs/embeddinggemma/model_card_2)，Apache-2.0；[GGUF 转换](https://huggingface.co/ggml-org/embeddinggemma-2-GGUF)使用修订 `bfcd298762cc34d0357ece5ebdd31791a3a374d8`。
- [llama.cpp b11476](https://github.com/ggml-org/llama.cpp/releases/tag/b11476)，MIT；使用官方平台归档和 Release asset SHA-256。
- [LaMa](https://huggingface.co/opencv/inpainting_lama)，Apache-2.0；使用固定文件大小和 SHA-256。
