# 工具与任务示例

先通过 `tools/list` 获取实时 Schema，枚举和嵌套字段使用该 Schema。下表只指导选工具，不替代参数契约。

| 目标 | 工具 |
| --- | --- |
| 功能/分类预设 | `avamedia_capabilities` |
| 宿主版本、后台状态、CPU 策略 | `avamedia_get_runtime`、`avamedia_set_resources` |
| 模型支持、安装、下载进度 | `avamedia_list_models`、`avamedia_download_model` |
| 本机目录和媒体轨 | `avamedia_list_files`、`avamedia_probe` |
| 视频/音频/图片转换、压缩、剪辑、PDF、ZIP、下载等 | `avamedia_create_task` |
| 标签/服装及拍摄组分类 | `avamedia_tag_media`、`avamedia_classify_media` |
| 字幕草稿/人物区间/视频总结 | `avamedia_transcribe_media`、`avamedia_detect_people`、`avamedia_summarize_video` |
| 查询、等待、控制 | `avamedia_list_tasks`、`avamedia_get_task`、`avamedia_wait_task`、`avamedia_control_task` |
| 日志和完整 JSON 报告 | `avamedia_get_task_log`、`avamedia_export_task_result` |
| 分类结果复制到文件夹 | `avamedia_preview_organization`、`avamedia_apply_organization` |
| 按模板/AI 关键词改名 | `avamedia_preview_rename`、`avamedia_apply_rename` |

创建返回 durable task ID。状态为 Waiting / Running / Paused / Stopping / Completed / Failed / Cancelled。`startImmediately=false` 只加入队列；`control_task(action=start)` 启动。运行中的暂停在安全检查点生效；停止后可能存在部分结果。移除会删除队列记录和任务元数据，保留媒体输出。下载模型也用队列 ID；查询结果含安装目录。

已通过桌面内部流程解锁的工作区还会注册 `avamedia_preview_vl_rename` 和 `avamedia_undo_vl_rename`；以实时工具列表为准。前者使用与桌面相同的文件夹规则和本地 Qwen3.5 VL 引擎，`mode=local` 需要已安装视觉模型与推理工具，`mode=rules` 仅解析名称。预览会等待分析完成，不创建任务也不改名；保存返回的 `plan`，检查或编辑目标文件名后交给 `avamedia_apply_rename` 并等待队列完成。撤销传这次改名的 `taskId`，复用原事务日志；响应丢失后重用同一 ID，`alreadyUndone=true` 表示没有再次修改文件。未解锁工作区不提供这些内部接口，也不提供 MCP 激活流程。

## 相似服装

先读取 `avamedia_capabilities.classificationPresets`，选取 ByOutfit / byOutfit 为 true 的实际对象，放入 rules 数组。其它规则不要一并传入。每次生成新的 UUID，不复制示例 UUID 用于多次任务。

```json
{
  "requestId": "bff3cba2-985c-4602-bc46-a3e8134926c8",
  "paths": ["/Users/example/Downloads/测试"],
  "recursive": true,
  "analysis": { "videoFrames": 8, "reuseSimilarFrames": true, "batchSize": 4 },
  "startImmediately": true
}
```

提交前必须添加 `rules: [实际返回的相似服装预设]`。缺省或空 rules 会启用默认分类组。数据涉及敏感内容时沿用宿主 NSFW 开关，不擅自修改。

用 `avamedia_wait_task(taskId, timeoutSeconds=30, includeResult=true, limit=200)` 等待，Completed 后读取所有结果页。比较指定文件的相同 RuleId 下 CategoryId 和 OutfitShootName，同时检查 NeedsReview。Backend 和 OutfitBackend 是推理路径记录，不等于 GPU 使用率测量。

文件夹落盘：`preview_organization(taskId, outputFolder, splitTypes=true, writeText=true)`；将返回对象完整作为 `apply_organization(requestId, preview)` 的 preview 参数，等复制任务完成。路径按现有规则生成，例如 `视频/拍摄组 05/01 裤袜/IMG_3576.mp4`；以实际预览为准，不能写死组编号。工具复制、去重、写标签报告，默认排除待确认项。

报告：`export_task_result(taskId, path=/absolute/new-report.json)` 导出全部分页数据。JSON 包含 Task、Partial 和 Pages；分类文件位于每一页的 Result.Files。分析任务和复制任务分别报告，不能将只有分类判断的任务说成已生成媒体文件夹。

## CPU 预留

```json
{ "settings": { "cpuReservePercent": 25 } }
```

传给 `avamedia_set_resources`。返回配置和有效线程/每工具并发预算；未指定字段保留。资源设置影响宿主全队列，不只当前请求。25% 预留并不保证所有时间都至少空闲 25%。

## 其它任务

转录：`transcribe_media(requestId, paths, speech={language:"zh"})`，返回带时间的 Cues；本工具不编码视频。人物分析：`detect_people(requestId, paths)`，要求宿主启用 Beta；输出片段需要 create_task 的 person-clip 功能和导出选项。

总结：`summarize_video(requestId, paths, outputFolder)` 默认本地模型。Online 模式使用用户已配置的服务并发送证据，按用户选择执行。普通转换通过能力列表的 featureId 配合 create_task；省略 options.format 沿用功能格式默认值。

长时间模型加载时查看任务 Activity、日志和模型状态，保留已接收任务；不要因进度暂未增加就重启宿主、切换全部模型到 CPU 或重复推理。
