# 云端视频总结 E2E 验证（2026-10-13）

执行验证：GPT 6 luna 子 Agent，通过 AvaMedia MCP 提交和导出。

**结论：整体未通过，但云端 Agent 工具自主性在唯一重试中得到部分验证。** 首轮在记录观察阶段发生供应商超时；按授权使用相同配置做了一次重试。重试成功自主调用两批 `get_video_frames` 并记录两条观察，但最终整理要点时供应商返回格式错误 JSON，任务仍失败，没有生成最终总结。

| 检查项 | 结果 |
| --- | --- |
| MCP 任务创建及云端路由 | 通过；两轮均由 MCP 创建，Activity 显示“线上视频总结 / 线上 API”，模型标识为“小红书 · dots3-note-prev”。 |
| 视觉种子帧描述 | 部分通过；两轮收到相同三张种子帧。首轮三张均有观察；重试只记录种子帧 AF001 与新增帧 AF004。 |
| Agent 自主工具 | 重试中证实部分自主使用；两批 `get_video_frames` 均完成、两次 `record_observation` 均完成。未调用 transcript 或 sequence 工具。 |
| 最终总结 | 未生成；首轮供应商超时，重试在最终要点整理时发生 JSON 解析失败。 |
| 语义核对 | AF002 人物及围观场景描述与源帧基本一致；AF003 未说出画面中舞者头部支撑、双腿抬起的倒立姿态，只笼统描述为地垫上的 floor move。重试 AO002 对 AF004 的描述与源帧中紫色上衣、棕色裤子的舞者站立姿态大致吻合；“准备”属于模型解释，静态帧不能确认意图。 |
| 音频 | Whisper Base 返回 0 条字幕；本轮没有完成音轨听辨，不能据此推断无声、音乐或其他声音内容。 |

## 执行条件

- 素材：Wikimedia Commons [The guys break dancing in the street near the mall.webm](https://commons.wikimedia.org/wiki/File:The_guys_break_dancing_in_the_street_near_the_mall.webm)，片长约 69.7 秒。它是街边霹雳舞表演，不是商场闲逛素材，因此只能验证这类表演/围观场景，不能外推到商场闲逛总结。来源页标注 CC BY 3.0，作者 KazanCity1。
- 输入文件：`artifacts/summary-agent-e2e-20261013/download/Download-20261013-195730.mkv`，SHA-256 `4374dbde5bd599ea7df0b487df2c730ff35b4d4520ccaf6feb38a6bc3af80257`。此文件此前已由 AvaMedia MCP 视频下载工具获取；本轮复用同一下载结果。
- 提交：requestId `57a07bfd-f406-48d9-a343-19385b0fad74`，taskId `ff715b43-04ce-4c35-9bca-cee948eb87ab`。
- 配置：provider `Online`；用户已配置的 provider 名称“小红书”，视觉与总结模型均为 `dots3-note-prev`；语音识别为 Whisper Base，视频候选帧数 12，语言简体中文。报告不包含凭据、密钥或认证数据。
- 宿主：隔离 MCP endpoint `http://127.0.0.1:18920/mcp`，应用运行时版本 1.1.85.0。部署指纹与构建证据见 `artifacts/summary-agent-e2e-20261013/host-build-evidence-cloud.json`；该证据对应本次运行源码，不代表其他构建或最终默认模型已通过 E2E。

## 结果与限制

### 首轮

任务到达 40% 的“记录可见观察”阶段，Activity 更新时间为 `2026-10-13T12:33:21.042278Z`。终态为 `Failed`，错误为“供应商请求超时”。MCP 任务日志记录 TLS response 读取时的 `TaskCanceledException` 和 `SocketException (89): Operation canceled`，调用链经过 `OnlineAiClient.SendAsync`、`OnlineSummaryModel.CompleteAsync`、`SummaryAgentHarness.Session.GetResponseAsync` 和 `VideoSummaryFrameAgent.RunAsync`。供应商单次请求配置超时为 180 秒。没有自动切换到本地模型或其他云供应商。

部分结果包含三个种子帧：AF001 5.003 秒、AF002 36.503 秒、AF003 63.883 秒，task API 的部分结果含 AO001–AO004。虽然 `get_task` 返回的 `agentTrace` 为空，但在该 task 的应用数据目录另有持久化 trace，已原样复制为 `artifacts/summary-agent-e2e-20261013/summary-cloud-first-agent-trace.json`。它记录了三次成功的 `record_observation`，分别关联 AF001、AF002、AF003；Turn 4 对 AF001 的重复观察只有 `requested` 状态。AO004 与 AO001 都指向 AF001 / 5.003 秒，文本近似重复，不应视为新画面。trace 标记 `Fallback=false`。本轮没有证据显示 Agent 自主调用取帧、序列观察或 transcript 工具。

视觉核对使用同一源文件提取了 AF002、AF003 对应时刻的帧，见 `artifacts/summary-agent-e2e-20261013/summary-cloud/source-frame-check/`。AF002 对照帧可见前景成人背身抱着戴粉色花帽的孩子，背景有表演者与围观者；模型描述基本吻合。AF003 静态对照帧可见黑衣舞者头部/上身支撑、双腿抬起的倒立姿态；模型未提及倒立姿态，仅概括为 floor move。单帧不足以判断动作是否旋转或其过程。因此，这些种子帧描述不足以支持完整活动过程总结。空转写不构成音频内容证据；本轮未听辨原片音轨，无法判断是否有音乐。

首轮没有最终总结；应用目录的 trace 显示三条观察成功、一次重复帧观察仅发出请求。MCP `get_task` 却返回空 `agentTrace`，表明任务结果接口没有同步这份已持久化的 trace。两轮均未返回供应商 token/usage 或费用数据。

### 唯一重试

- 提交：requestId `c39eada3-b32f-4999-ab90-f00d3b2c4769`，taskId `1ecd63a7-fc1c-4854-b46a-14aef55dcd94`。使用同一输入文件、Online provider、`dots3-note-prev` 视觉和总结模型、Whisper Base、12 个候选帧、中文输出及原 focus，输出目录为 `artifacts/summary-agent-e2e-20261013/summary-cloud-retry/`。供应商单次超时保持 180 秒；未改配置、模型或源码。
- Activity 进入 80%“整理要点与关键词”，backend 为“线上 API”。持久化 trace 与最终 task 结果都记录了 `get_video_frames` 两次成功，参数分别为 `[15,25,45]` 和 `[10,55,68]` 秒，每次返回 3 帧；对应实际帧时间分别为 AF004 14.983、AF005 24.983、AF006 44.983，以及 AF007 9.983、AF008 54.983、AF009 67.983 秒。`record_observation` 对 AF001 与 AF004 的两次调用均完成。`Fallback=false`。因此，本轮实际证明模型可以自主补取新时刻并登记观察。
- AO002 描述 AF004 上紫色上衣、棕色裤子舞者站立准备动作；对照源帧见 `summary-cloud-retry/source-frame-check/AF004-source.png`，衣着与站立姿态大致吻合，但“准备”不作为已核实意图。尽管模型获取了包含 67.983 秒的 AF009，Agent 没有把该后段帧登记为 observation，也没有完成时间序列观察；不能据此证明总结覆盖了视频后段动作。
- 任务终态 `Failed`，进度 80%，错误为 `'}' is invalid without a matching open. LineNumber: 0 | BytePositionInLine: 928.` 日志调用栈位于 `VideoSummaryGrounding.ParseResponse` / `VideoSummaryOutline.ParseAsync` / `VideoSummaryService.GroundedSectionsAsync`。最终 sections、chapters、highlights、keywords 均为空。云端完成了部分帧分析和工具闭环，但总结生成阶段没有产出有效结果。

这一轮是按指示执行的唯一重试；不再扩大测试或再次提交。综合判定仍为 E2E 未通过：重试补足了工具自主性证据，但没有可评估的最终总结。

## 原始证据

- 请求参数：`artifacts/summary-agent-e2e-20261013/summary-cloud-request.json`
- MCP 入队响应：`artifacts/summary-agent-e2e-20261013/summary-cloud-submission.json`
- 最终任务和部分结果：`artifacts/summary-agent-e2e-20261013/summary-cloud-final-task.json`
- MCP 部分结果导出响应：`artifacts/summary-agent-e2e-20261013/summary-cloud-export-response.json`
- 部分结果导出文件：`artifacts/summary-agent-e2e-20261013/summary-cloud/exported-cloud-result.json`
- 任务日志及超时调用栈：`artifacts/summary-agent-e2e-20261013/summary-cloud-task-log-final.json`
- 第一轮应用目录持久化 trace：`artifacts/summary-agent-e2e-20261013/summary-cloud-first-agent-trace.json`。它与 MCP `get_task` 中 `agentTrace: []` 不同；前者显示三条完成的记录观察、一次未完成的重复请求。此差异是结果同步/可观测性缺陷，不能据 API 空数组断言没有执行工具。
- 重试请求/入队响应/终态任务/partial导出响应：`summary-cloud-retry-request.json`、`summary-cloud-retry-submission.json`、`summary-cloud-retry-final-task.json`、`summary-cloud-retry-export-response.json`。
- 重试 MCP partial 导出及日志：`summary-cloud-retry/exported-cloud-retry-result.json`、`summary-cloud-retry-task-log.json`。
- 重试持久化 trace：`summary-cloud-retry-agent-trace.json`；源帧核对图：`summary-cloud-retry/source-frame-check/AF004-source.png`、`AF009-source.png`。
- 运行时/模型/工具清单快照：`artifacts/summary-agent-e2e-20261013/cloud-avamedia_get_runtime.json`、`cloud-avamedia_list_models.json`、`tools-list.json`
- 源帧核对图：`artifacts/summary-agent-e2e-20261013/summary-cloud/source-frame-check/AF002-source.png`、`AF003-source.png`

测试后已修复 MCP 部分结果未同步持久化 AgentTrace 的问题；仅进行相关编译检查，未为此新增云端请求。超时和总结 JSON 错误仍按原始失败结果记录。测试宿主已停止，临时凭据引用已移除，正式供应商设置未修改。
