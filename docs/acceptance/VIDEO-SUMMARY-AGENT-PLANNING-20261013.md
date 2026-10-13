# 视频总结 Agent v5 端到端复测 · 2026-10-13

执行：GPT 6 luna 子 Agent；主 Agent 复核关键源帧、任务结果与报告。任务通过 AvaMedia MCP 的真实持久队列执行。

## 结论

同一段 69.702 秒街边霹雳舞视频，本地 Qwen3.5 4B 与云端小红书 `dots3-note-prev` 各提交一次。两项 MCP 总结任务均在 80%“整理要点与关键词”阶段以“模型未能生成有依据的内容章节”失败，`outputIsPartial=true`；最终 sections、chapters、highlights、keywords 为空。因此两轮的完整视频总结端到端验收都未通过。

Agent 调查闭环与总结任务要分开判断：本地 Agent 写出了 `planned`/`nextCheck`，成功记下一条有效观察，实际调用了 `get_video_frames`，但重复记录 AF002 两次后由 harness 记录 `Agent decision correction exhausted`，没有 `finish`，且 AF003 没有有效观察。云端 Agent 记录了三张种子帧并发出 `finish`，其调查闭环通过；最终章节生成仍失败，所以不能把云端 Agent 的 finish 当成总结任务成功。

## 执行条件与宿主

- 输入：`artifacts/summary-agent-e2e-20261013/download/Download-20261013-195730.mkv`，SHA-256 `4374dbde5bd599ea7df0b487df2c730ff35b4d4520ccaf6feb38a6bc3af80257`，时长 69.702 秒。结束后哈希一致。
- 宿主：隔离 MCP endpoint `http://127.0.0.1:18920/mcp`，运行时 1.1.87.0。当前源码部署指纹、Agent v5 和构建证据见 [`host-build-evidence.json`](../../artifacts/summary-agent-e2e-20261013/planning-v5/host-build-evidence.json)：source commit `80757c126c3e0157c32bf96c16e21d3d3bd7c262`，Desktop Release 构建 0 warning / 0 error。
- 测试参数：Whisper Base / Speech、12 帧候选、简体中文，同一 focus：“请只总结视频中可直接观察的动作和场景，环境音如不能支持明确结论则注明不确定。”请求按要求分别指定本地 4B 与已配置云端 provider，未修改全局 provider 配置。
- 两次任务分别只提交一次；无重试。耗时按本地请求文件写入到终态 task 文件写入的文件时间估算：本地约 4 分 35 秒，云端约 4 分 53 秒。活动阶段的 `updatedUtc` 也保留在终态 task JSON 中。

## 本地 4B 轮

- 配置：本地 `summary-qwen35-abliterated` 同时用于视觉和总结，Whisper Base。MCP requestId `f505d27c-b59a-4b40-bf43-2c69c37c05c0`，taskId `b7c38704-8c38-4ae5-974d-137040da91c4`。Activity backend 为 `Metal / CPU`。模型清单包含 Whisper Base 与 Qwen3.5 4B Q4。
- 任务状态：Failed，80%，partial；异常位于 `VideoSummaryService.GroundedSectionsAsync`，完整错误日志见 `local-task-log.json`。MCP `export_task_result(allowPartial=true)` 导出 1 页。
- Harness trace 共 18 条：4 条计划事件均含 `plan`/`nextCheck`；AF001 成功写成 AO001。Agent 为核对 AF002 请求 36.503 秒右下半区 crop，工具实际返回本任务专属 AF004 PNG；它显示粉帽孩子和蓝衣蹲者的局部，与源帧一致。Agent 随后却两次用相同橙衣舞者描述调用 `record_observation(frameId=AF002)`，两次均返回相同 AO002，说明重复观察被去重。源帧 AF002 中是成人抱着粉帽孩子，故 AO002 是跨帧人物误认。AF003（63.883 秒）没有被记录。
- trace 含两条 identical-call `correcting`，`segmentNotes` 明确记载 `Agent decision correction exhausted`。trace 未出现 `finish`、`finish_rejected` 或 `abstain`；结果字段 AF003 的“未形成可确认观察”是缺失观察占位，不是模型原话。Harness 没有走完调查门槛。
- `rejectedClaims=0` 是结果快照计数；不能据此推断候选均被审核通过。最终章节为空，任务日志没有给出逐条 claim 拒绝原因。

## 云端轮

- 配置：已配置的小红书 provider，视觉和总结模型均为 `dots3-note-prev`，Whisper Base；MCP requestId `94537438-953a-43bc-bfbd-95ca18c2736e`，taskId `16c50c58-dfee-433c-be67-df4be3f258b3`。Activity backend 为 `线上 API`。
- Agent trace 共 14 条：4 条计划事件均含 `plan`/`nextCheck`；AF001、AF002、AF003 三条观察都成功记录为 AO001–AO003。第 4 轮 nextCheck 写明“调查已结束，无进一步检查项”，并发出 `finish`。该 finish 对视频的概括是街边街舞表演，分别提到 5 秒处橙衣舞者地板动作、36 秒处成人抱着粉帽孩子与旁观者、63 秒处黑衣舞者头部支撑或倒立。
- 任务仍在 80%“整理要点与关键词”失败，partial MCP 导出 1 页；最终章节、摘要、关键词为空。因此云端 Agent 调查闭环通过，但完整总结任务失败。
- `segmentNotes` 没有格式/结构纠正记录；本轮结构纠正分支未覆盖。trace 没有 `finish_rejected` 或 `abstain`。`rejectedClaims=0` 仅是结果快照值；当前日志只显示章节生成异常，没有逐条拒绝原因。无法区分章节没有生成、grounding 过滤或后续语义核验过滤，不将失败归因于模型能力或 JSON 格式错误。

## 帧与内容核对

本次两个 task 专属目录中的 AF001–AF003 图像哈希相同。核对图集中在 [`planning-v5/source-frame-check/`](../../artifacts/summary-agent-e2e-20261013/planning-v5/source-frame-check/)；其中 `local-AF004-actual.png` 是本地 task 的 AF004 原图副本，SHA-256 `b9b1956d37259985ef9e0828d3396a9bab1cb626ca823388816b3eb03eb291d6`。它与请求区域相符。早先误打开了历史全局 summary 目录中的同名 AF004，现以 task 专属目录图像为准；本报告没有使用那张历史 PNG 对本轮作结论。

- AF001（5.003 秒）：橙衣、蓝裤舞者在垫子上做地板动作，观众围看。两路帧描述都基本准确。
- AF002（36.503 秒）：前景成人抱着戴粉色花帽的孩子，背景观众围着垫子。云端描述基本准确；本地误把 AF001 的橙衣舞者描述套到 AF002，且未吸收 AF004 的局部观察。
- AF003（63.883 秒）：黑衣舞者头部/上身接近垫面、双腿抬起，周围有人观看。云端观察提到头部支撑或倒立，符合静态帧；本地没有形成该帧观察。单帧不能证明动作过程、完整连续性或舞者意图。云端未调用序列观察工具。
- 两轮 `subtitleCount=0`、Transcript 为空。未听辨音轨，因此不能据此判断静音、音乐或环境声。

## 原始证据

所有本轮证据保存在 [`artifacts/summary-agent-e2e-20261013/planning-v5/`](../../artifacts/summary-agent-e2e-20261013/planning-v5/)；既有验收报告和旧结果未覆盖。

- 运行时与契约：`capabilities.json`、`runtime.json`、`models.json`、`tools-list.json`。
- 本地：`local-request.json`、`local-submission.json`、`local-final-task.json`、`local-exported-result.json`、`local-export-response.json`、`local-task-log.json`、`local-wait-01.json` 至 `local-wait-07.json`。
- 云端：`cloud-request.json`、`cloud-submission.json`、`cloud-final-task.json`、`cloud-exported-result.json`、`cloud-export-response.json`、`cloud-task-log.json`、`cloud-wait-01.json` 至 `cloud-wait-07.json`。
- 画面：`source-frame-check/source-5.003.png`、`source-36.503.png`、`source-63.883.png`，本地与云端 task 专属 AF PNG 副本，以及 `local-AF004-actual.png`。

本轮只执行了用户指定的同一视频本地与云端各一轮总结；未增加样本、运行性能/UI/回归测试或重试。没有人工听辨音轨，也未把静态帧描述扩写成动作连续性结论。

已覆盖计划记录、局部补帧、重复观察去重、调查正常结束及协议纠正耗尽。旧完整截图的缓存回看、`mark_frame_uncertain`、`finish_rejected` 和总结 JSON 结构纠正本轮未触发，不能宣称这些分支已通过端到端验证。

测试后已停止本轮隔离宿主并删除临时 state 中的凭据文件；原有用户配置与凭据存储保留。
