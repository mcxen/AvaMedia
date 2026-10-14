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

已通过桌面内部流程解锁的工作区还会注册 `avamedia_preview_vl_rename` 和 `avamedia_undo_vl_rename`；以实时工具列表为准。前者提交后台分析并立即返回持久任务 ID，使用 get/wait_task 读取全部结果分页；local 使用本地 VL，online 使用配置供应商，rules 仅解析名称。可用 providerId/model 显式选择本次供应商和模型，不改全局默认。分析不改名，关闭界面或断开连接不取消；检查或编辑返回的 Plan 后交给 `avamedia_apply_rename` 并等待队列完成。撤销传这次改名的 `taskId`，复用原事务日志；响应丢失后重用同一 ID，`alreadyUndone=true` 表示没有再次修改文件。未解锁工作区不提供这些内部接口，也不提供 MCP 激活流程。

## 原生选项与模型配置

先用 `get_settings` 读取配置、供应商 ID 和 revision，密钥仅返回 hasApiKey。`update_settings(patch, expectedRevision)` 按字段保留未指定项，支持隐私/Beta、输出、手动/自动线程、模型预加载/空闲释放和下载源等；只改用户授权的项。线上供应商通过 provider_presets/create_provider/update_provider/delete_provider 配置，apiKey 单独只写；修改地址需重新提供密钥。provider_models 默认缓存，refresh=true 明确访问线上。check_provider 默认仅本地校验；text/vision 是显式线上调用，vision 要求 mediaPath 并发送一帧，不在读配置时自动调用。

`list_models` 返回模型真实驻留/安装状态和目录。`inspect_model` 列出文件与哈希，verifyHashes=true 才做完整校验；download_model 可传本次 source；import_model 用共享队列导入服务器文件；release_models 释放空闲会话而保留安装，delete_model 卸载指定模型。两者含义不同，按用户要求调用。

`describe_feature` 提供全类别原生参数、默认值和专用路由，不能仅靠视频简化 form 判断参数是否支持。create_task 的部分 options 递归合并到功能默认；inputOptions 按显式文件一一设置合并/混流/混音参数。get_task_options 读取保存参数，update_task_options 仅编辑未启动等待任务，move_task 调整调度顺序。PDF 页码用 document_pages；下载链接用 inspect_download 后选择 download_videos。词库通过 list/read/save/delete_word_library 原生管理，候选直接传给任务。详细字段以实时 tools/list 和 [MCP 接口文档](../../../../docs/MCP.md) 为准。

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
