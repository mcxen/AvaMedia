# 长视频总结 v2

默认时长超过 20 分钟且需要 AI 输出时，`VideoSummaryService` 使用长视频流水线；20 分钟以内及仅提取字幕的任务保留既有流程。`VideoSummaryOptions.LongVideoThresholdMinutes` 可调高，不能调低至影响原短视频范围。

复用媒体探测、Whisper 300 秒识别块、连续低分辨率帧扫描、逐帧描述服务及共享本地模型租约。镜头候选在段内按镜头时长选择，视觉模式增加采样。音轨支持按偏好语言／默认轨选择及全部音轨；识别使用 auto，保留语言概率并复核低置信度 cue。字幕按语言匹配。

每个任务的内部结果目录为 `AiPartialResults.SummaryFolder(job)`：

- `work/manifest.json`：输入与内容哈希、已完成产物；临时文件完成后原子替换。
- `work/asr/track-N/chunk-XXXX.json`：300 秒块与 VAD、语言信息；语言复核单独缓存。
- `work/shots.json`、`work/frames/`、`work/signals.json`：独立的信号缓存；改变关注点不使其失效。
- `work/segments.json`、`work/run.json`：真实分段计划、当前运行标识。
- `work/map/sXXXX.json`：完成校验即写入并发布的分段结果；滚动上下文补跑存为 `sXXXX-repair.json`，补跑降级时保留原已完成结果。
- `work/reduce/`、`work/verify/`：有界归纳树与按原始证据预算分批的章节核验。
- `evidence/*.jsonl`、`evidence/index.json`：证据分片与字节偏移索引，引用按需读取。
- `report.json`：v2 报告，只含摘要、章节、要点、引用和覆盖信息，硬上限 4 MB。
- `segments.md`：取消或汇总失败时的完整已完成分段导出；完成时 `summary.md` 同样包含分段事件与要点。

桌面任务详情显示真实段状态及摘要；结果窗口优先显示“分段”，全片完成后补充概览和章节，点击时间点回看视频。设置窗口提供音轨、并发与严格顺序模式，有检查点的未完成任务可继续。

远程任务结果返回 `LongSummaryTaskSnapshot`（report、分页 segments、totalSegments），任务详情直接显示分段摘要、要点和事件。AI progress v2 的 `summarySegments` 同样带真实时间范围、状态和摘要；`/web/tasks/{id}/ai/results?after=N` 返回分页分段，`/segments` 返回段状态，`/summary/evidence/{id}` 按 ID 读取原文。客户端沿用现有刷新机制，不等待全片汇总。

预算以实际 JSON 请求计数。`ITokenCounter` 可接入 tokenizer；默认实现采用方案要求的 CJK 每字 1 token、其他每 3 UTF-8 字节 1 token估算，并保留系统／schema、输出及 10% 安全余量。长度截断先在模型输出上限内加大输出，仍截断则二分。429、5xx、网络与超时最多重试四次，遵守 Retry-After。回答缓存按模型身份、输入、预算和提示／schema 版本区分，LRU 上限 512 MB。

## 本地阶段检查

使用 `~/.dotnet/dotnet build <相关项目> -p:EnableWindowsTargeting=true`。指定输出目录时可加 `--artifacts-path <目录>`；磁盘不足时可把构建与临时目录放到内存盘。各阶段只执行对应测试入口：

```sh
dotnet AvaMedia.VideoSummaryTests.dll long-phase 1 # 三档各 10000 个随机预算用例
dotnet AvaMedia.VideoSummaryTests.dll long-phase 2 # 36 个合成 ASR 块，第 12 块崩溃后只跑余下 25 块
dotnet AvaMedia.VideoSummaryTests.dll long-phase 3 # 三小时 cue／镜头／静音表，连续覆盖与预算
dotnet AvaMedia.VideoSummaryTests.dll long-phase 4 # 假模型重试、缓存、修复、截断、并发
dotnet AvaMedia.VideoSummaryTests.dll long-phase 5 # 三小时章节、1000 段树高及请求上限
dotnet AvaMedia.VideoSummaryTests.dll long-phase 6 # 无语音、多语言、双音轨与核验批次
dotnet AvaMedia.VideoSummaryTests.dll long-phase 7 # 三小时端到端、取消续跑、即时分段、汇总失败与证据读取
dotnet AvaMedia.ProgressTests.dll long-summary # 分段状态与远程 DTO
```

三小时测试完全由程序合成时间线并使用假模型，不生成真实三小时媒体，不下载或启动实际模型。短视频使用既有 `mock` 测试入口。

## 已知限制

- 默认 `NullOcrEngine`；可通过 `VideoSummaryService` 注入 `IOcrEngine`。无语音且缺少 OCR 时报告明确提示文字覆盖限制，当前没有内置 OCR 模型。
- token 默认是保守估算，尚未接入 GGUF／tiktoken 的精确 tokenizer。
- 极端字幕密度、跨音轨连续重叠或单个超长完整句可能无法同时满足句子边界、60–480 秒段长和输入预算；此时明确报错，不发送超预算请求。
- 全局远程并发按供应商／模型身份共享，首次建立的 gate 决定该进程中同一身份的并发容量；本地始终为 1。
- 本轮未进行真实三小时模型推理、媒体输出或截图／像素验证。
