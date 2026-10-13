# 本地视频总结 Agent 端到端验收 · 2026-10-13

> 执行验证：GPT 6 luna 子 Agent；修复实现：GPT 6.1 sol 子 Agent 与主代理。
> Baseline：当前源码第一轮运行，记录工具自主性问题；后续修复复测单独留档。
> 最新状态：第三轮显式指定本地 Qwen3.5 4B 同时做视觉和总结时，MCP 任务在“整理要点与关键词”失败，无完整总结；详见“4B 同模型复测”。因此最新验收仍未通过。

## 三轮运行总览

| 轮次 | 配置与状态 | 自主观察结果 | 总结结果 |
| --- | --- | --- | --- |
| 1 · baseline | 原实现，0.8B text + 4B vision；Completed | 一次重复种子帧的取帧调用；无成功 record_observation，使用静态帧 fallback | 只写5秒处舞者，漏63.883秒倒立 |
| 2 · Harness修复 | typed decision、finish/abstain，仍为0.8B text + 4B vision；Completed | 三帧各有fallback observation；Agent未成功record_observation | 引用三帧、包含后段倒立；音乐关键词没有声音证据 |
| 3 · FrameAgent v4 + 4B text | 4B vision + 4B text；Failed、80%、partial | 三次record请求被接受，其中一条重复；AF003未形成observation | 4B未生成有依据的内容章节，没有完整摘要 |

第三轮既加入Agent/Harness与静态观察工具修复，又更换总结模型，不能当作4B与0.8B的单变量A/B比较。它能证明4B路由被实际调用，但不能回答“4B普遍比0.8B聪明吗”。

## 第一轮结论

| 验收项 | 结论 |
| --- | --- |
| 实际链路 | 通过：当前源码 DLL 后台 MCP 宿主、本地队列、MCP 视频下载、本地 Whisper 与视觉/文本模型及报告落盘均完成。 |
| Agent 工具自主性 | 未通过：有一次 `get_video_frames` 工具调用，但取到的三个时间点重复种子帧附近；没有一次成功的 `record_observation`。本例转写为空，未调用 `read_transcript` 不构成缺陷；没有使用区域裁剪本身也不判失败。最终 observations 全来自种子帧的后备单帧描述。 |
| 语义能力 | 部分通过：能认出画面中舞者与观众，未凭空编造商场闲逛或对白；但总结仅依据 5 秒单帧，漏掉 63.883 秒清楚可见的另一名舞者倒立动作，无法证明覆盖整段主要过程。 |

任务 Completed 和报告文件存在只证明执行链完成；这次样本没有通过自主观察闭环门槛。

## 运行环境与输入

宿主是从当前 checkout `93b1acf` 构建并启动的 Desktop DLL，Release 构建 0 警告、0 错误；源码 DLL 与 Desktop 实际加载的 Core DLL 哈希一致。运行时版本 `1.1.85.0`，macOS 15.7.1，MCP 后台模式，隔离状态目录 `artifacts/summary-agent-e2e-20261013/state`，当时队列为空。构建和装载证据见 [host-build-evidence.json](../../artifacts/summary-agent-e2e-20261013/host-build-evidence.json)、运行状态见 [avamedia_get_runtime.json](../../artifacts/summary-agent-e2e-20261013/avamedia_get_runtime.json) 和模型清单 [avamedia_list_models.json](../../artifacts/summary-agent-e2e-20261013/avamedia_list_models.json)。

输入是 Wikimedia Commons 的 [The guys break dancing in the street near the mall.webm](https://commons.wikimedia.org/wiki/File:The_guys_break_dancing_in_the_street_near_the_mall.webm)，页面列出 69.686 秒、1280×720、VP8/Vorbis，CC BY 3.0，作者 KazanCity1。按页面提供的原始文件链接，通过 `avamedia_create_task` 的 `download` 功能下载，任务 `4fdc10da-6c54-4b07-87d4-b2e00b37854e`，requestId `ff9dd0c5d6134541a1e1b5eec459e69d`。输出为 [Download-20261013-195730.mkv](../../artifacts/summary-agent-e2e-20261013/download/Download-20261013-195730.mkv)，probe 时长 69.702 秒、1280×720、VP8 视频、Vorbis 44.1 kHz 双声道音频；SHA256 为 `4374dbde5bd599ea7df0b487df2c730ff35b4d4520ccaf6feb38a6bc3af80257`。总结前后重新计算哈希相同，源输入文件未被总结任务改写。下载调用和状态证据在 `artifacts/summary-agent-e2e-20261013/download-request-2.json`、`download-submission-2.json`、`download-wait-2.json`、`probe.json`。第一次对原始文件路径的人工猜测 `/8/87/` 得到 HTTP 404；依据 Commons 页面链接改为 `/6/6c/` 后成功。失败请求也保留在 `download-request.json` / `download-wait.json`，不计为成功下载。

## 本地总结执行与语义核对

任务 `a894f006-90da-465f-9280-73da95d78981`，requestId `e92f6fe698d543148fe5656c323eb797`，通过 `avamedia_summarize_video` 提交，指定 `provider=Local`、视觉模型 `summary-qwen35-abliterated`（Qwen3.5 4B Q4），语音来源 `Speech`、Whisper Base，12 帧候选，简体中文输出。未配置或选择 Online provider。模型清单显示 4B 视觉、0.8B 文本和 llama.cpp runtime 已安装；运行期 Activity 曾报告 backend `Metal / CPU`。结果 `Models` 明确列出 Whisper Base、Qwen3.5 4B Q4、Qwen3.5 0.8B Q8；最终摘要由本地 0.8B 文本模型生成，视觉帧由本地 4B 模型描述。任务/参数完整证据见 [summary-final-task.json](../../artifacts/summary-agent-e2e-20261013/summary-final-task.json)、[summary-request.json](../../artifacts/summary-agent-e2e-20261013/summary-request.json)、[exported-result.json](../../artifacts/summary-agent-e2e-20261013/exported-result.json)，模型原始产物在 [summary/Download-20261013-195730](../../artifacts/summary-agent-e2e-20261013/summary/Download-20261013-195730/)。

报告采样图 AF001（5.003 秒）显示橙色上衣、蓝裤子和紫头带的舞者坐在垫上活动，周围有人观看；AF003（63.883 秒）显示另一名舞者在垫上倒立，观众围观。Agent 直接查看这两帧与 Qwen 4B 的描述基本相符。最终摘要只写了 AF001 画面中的舞者和观众，未使用 AF002/AF003 的事实，也没有概括 63 秒处的倒立动作。场景类别“舞蹈/表演”基本正确；这是街头舞蹈素材，不是逛商场素材，不能据此推断商场闲逛能力。

## 工具 Trace 与语音限制

完整 [agent-trace.json](../../artifacts/summary-agent-e2e-20261013/summary/Download-20261013-195730/agent-trace.json) 记录一项 `get_video_frames`（返回三帧）、一次无效决策修正和 final。服务先创建种子图 AF001–AF003（5.003、36.503、63.883 秒）；Agent 工具调用随后生成 AF004–AF006（5.003、36.503、63.503 秒），因此确实触发过取帧工具，但基本重复已有时间点，没有找到新的视角。最终没有任何 `record_observation` 成功调用；本例转写为空，未调用 `read_transcript` 不构成缺陷，区域裁剪也不是必须条件。trace 的 `Fallback=true`，不确定项说明 Agent 未增补有效观察；AO001–AO003 是系统回退后按三张种子图单帧生成的 observations，不能冒充 Agent 主动记录。`RejectedClaims=5` 表示 grounding 丢弃了 5 条候选主张；这是内容过滤数，不是模型拒绝数。`RefusedFrames=0` 且 `SummaryRefused=false`。

音轨存在（本轮曾将 10–13 秒片段送入音频输出 helper，但当前执行视图只返回音频内容占位，我无法辨听；未能完成头/中/尾的音轨听辨，音频语义证据不足）。Whisper Base 没有产生可用字幕或语音片段：`SubtitleCount=0`、`Transcript=[]`。输出保留 Whisper 来源及“语言检测置信度不代表转写可信度”“没有可用对白证据”等限制。没有根据空转写把音频判成静音、音乐或环境声，也没有据声音扩写主题。由于无识别文本，此样本无法验证语音内容对照或 transcript 工具调用；原始媒体本身保留音轨。

## 复查范围与限制

这是一个 69.7 秒、单样本的定向端到端测试。它确认 MCP 下载、本地推理、报告导出及输入文件保护；未验证普遍语义准确率、商场/闲逛场景能力、完整连续动作重建或语音识别准确率。下一步若继续修复，先让 Agent 决策稳定地产生有差异的时间取帧并成功调用 `record_observation`，避免同一初始种子被重复查询；随后仍用本条素材复测同一闭环范围。

## 修复复测 · 2026-10-13

主代理修复 `SummaryAgentHarness` 的 typed JSON arguments、finish/abstain 约束与 trace 细节后，重新构建并启动当前未提交源码 DLL；构建 0 警告、0 错误，精确部署指纹见 [host-build-evidence-retest.json](../../artifacts/summary-agent-e2e-20261013/host-build-evidence-retest.json)。宿主仍为隔离 MCP state、版本 1.1.85.0；模型和视频未更换。重新通过 `avamedia_summarize_video` 提交相同本地 4B vision + 0.8B text、Whisper Base/Speech、12 帧候选和同一输入文件，requestId `8a3a952483e84caea5e1ecf9ca0a6471`，taskId `b07106ae-6f0d-42ea-8691-15c3ccb0d5b5`，输出独立位于 `artifacts/summary-agent-e2e-20261013/summary-retest/Download-20261013-195730/`。MCP 状态 Completed、100%、outputIsPartial=false；导出结果为 [exported-retest-result.json](../../artifacts/summary-agent-e2e-20261013/exported-retest-result.json)，最终任务结果为 [summary-retest-final-task.json](../../artifacts/summary-agent-e2e-20261013/summary-retest-final-task.json)。

复测保留同样三张种子图 AF001–AF003（5.003、36.503、63.883 秒），由 Qwen 4B 描述。总结的摘要现在引用三条对应画面证据，明确包括 63.883 秒舞者倒立；相较 baseline，它没有再只写 5 秒处的坐姿舞者，因此后段动作覆盖有所改善。Agent 直接查看 AF003，确实是舞者在垫上倒立、周围观众观看。标题/章节集中于后段倒立动作。结果主要活动与画面相符，但没有把这条素材误说成商场闲逛。

Agent 自主闭环仍未通过。新 trace 有四次 `record_observation` 请求，但全部因模型将观察标为 `sequence=true` 被拒：其中既有单帧请求，也有间隔约 30 秒的帧组；sequence 约束要求两张以上相邻且间隔不超过 5 秒的画面。第 5 轮又请求已提供的相同三个种子时刻，被防重复规则拒绝；第 6 轮 `finish_rejected` 记录未能保存必需观察。随后 fallback 对真实种子帧生成 AO001–AO003，因此报告可读且 Completed，但这些观察仍不是 Agent 成功调用 `record_observation` 的产物。详细参数、拒绝原因和 `finish_rejected` 见 [summary-retest agent-trace.json](../../artifacts/summary-agent-e2e-20261013/summary-retest/Download-20261013-195730/agent-trace.json)。其中 AF002 请求文本还把抱着孩子的人描述成舞者蹲下；该请求因 `sequence=true` 的采样间隔规则失败，工具没有做图像语义审核，因此不能称为工具识别并拒绝了人物误认。这说明修复了初次无效结构决策的部分执行障碍，但模型仍未学会将静态事实用 `sequence=false` 记录；需要针对 Agent 的决策提示/格式适配继续修复。

复测关键词含“音乐氛围”，但音频证据没有文本支撑：Whisper Base 的 `SubtitleCount=0`、`Transcript=[]`。音轨为 Vorbis 44.1 kHz 双声道；当前执行环境无法音轨听辨音频，故音乐关键词虽引用了 AO001/AO002，但这两条引用只有画面描述，不能支持声音推断，属于未核实的音频语义外推，不能据此认定实际音轨内容。另有“服装色彩搭配和谐，突出人物主体”这类主观审美要点，没有画面事实依据，也不应作为 grounded observation。原始下载文件在复测后 SHA256 仍为 `4374dbde5bd599ea7df0b487df2c730ff35b4d4520ccaf6feb38a6bc3af80257`，未被总结任务改写。

第二轮（0.8B文本模型）结论：执行链通过；对关键后段动作的语义覆盖有所改善；Agent 自主观察工具闭环仍未通过；音频含义证据不足，且关键词“音乐氛围”应被视为未经验证的推断。只在同一素材和选项上复测了一次，没有扩展样本或运行性能/压力检查。

## 4B 同模型复测

第三轮在新构建宿主上再次使用同一视频和 Whisper Base 选项，并显式指定 `localSummaryModelId=summary-qwen35-abliterated`。本轮让同一个本地 Qwen3.5 4B Q4 同时承担视觉帧描述和最终文本归纳；不是严格的 4B 对 0.8B 单变量对照，因为同时包含 Harness/FrameAgent 修复、同模型路由和提示/证据变化。宿主部署指纹见 [host-build-evidence-local4b-retest.json](../../artifacts/summary-agent-e2e-20261013/host-build-evidence-local4b-retest.json)，参数与提交响应见 [summary-local4b-request.json](../../artifacts/summary-agent-e2e-20261013/summary-local4b-request.json) 和 [summary-local4b-submission.json](../../artifacts/summary-agent-e2e-20261013/summary-local4b-submission.json)。任务 `96f45c1f-521e-4bd8-93a6-88cd13214fcc`、requestId `1b30466e909b4c3c9a94bd167140e522` 最终为 Failed、80%、`outputIsPartial=true`；MCP 用 `allowPartial=true` 导出一页，见 [exported-local4b-result.json](../../artifacts/summary-agent-e2e-20261013/exported-local4b-result.json)。异常日志指出 `VideoSummaryService.GroundedSectionsAsync` 无法生成有依据的内容章节，未产出完整 summary 文件。故本轮不能作为完成的 4B 总结结果，也不能据此断言 4B 比 0.8B 更聪明。

4B vision 收到了三张种子帧 AF001–AF003，但没有保存 AF003（63.883 秒倒立画面）的对应 observation。报告中的“未形成可确认观察”是应用为缺失观察显示的占位文字，不是模型原话。它将 AF002（36.503 秒，成人肩上坐着戴粉帽的小孩）错写成“主要人物……正坐在垫子上”。trace 有三次 `record_observation` 请求和三次 completed：按帧与文本去重后只有两条唯一记录；AF001 同一文字重复两次（uncertainty 字段不同），AF002 一条错误描述；AF003没有记录。重复调用触发一次 harness duplicate-call correction，之后 AF002 调用仍可完成。去重后各记录为：AF001：“在 AF001（5.003 秒）中，主要人物身着橙色上衣与蓝色裤子，正坐在垫子上，身体略微前倾，双手向前伸展。”（同一文本实际记录两次）；AF002：“在 AF002（36.503 秒）中，主要人物身着粉色帽子与粉色上衣、蓝色裤子，正坐在垫子上，身体略微后仰，双手向后伸展。”（与画面中成人肩上坐孩童不符）；AF003：无成功观察记录。当前输出目录未生成 report/agent-trace 文件；MCP `get_task` 的部分结果中包含原始Trace。已从该响应提取保存 [summary-local4b-agent-trace.json](../../artifacts/summary-agent-e2e-20261013/summary-local4b-agent-trace.json)，完整部分模型结果及原Trace也保存在 [summary-local4b-current.json](../../artifacts/summary-agent-e2e-20261013/summary-local4b-current.json) 和 [exported-local4b-result.json](../../artifacts/summary-agent-e2e-20261013/exported-local4b-result.json)；异常堆栈见 [summary-local4b-task-log.json](../../artifacts/summary-agent-e2e-20261013/summary-local4b-task-log.json)。这表示 version 4 的静态观察入口可成功落记录，但工具自主性与视觉语义仍有问题；记录被接受只证明参数满足结构校验，不代表图像事实准确。

本轮 `Models` 列表记录 Whisper Base 与两次 Qwen3.5 4B Q4（vision/text），Activity backend 为 `Metal / CPU`。Whisper 仍为 0 字幕、空 transcript。未完整总结任务里没有可引用的音乐结论；前一轮“音乐氛围”虽带 AO001/AO002 画面引用，但这些引用不能支持声音推断。

第三轮结论：本地双 4B 路由确实运行，但最终总结阶段失败；自主记录数量增加仍未覆盖晚段关键动作，且出现 AF002 视觉误读。该轮不改变第一、二轮各自证据，也未证明 4B 优于 0.8B。

最终代码保留显式 4B 总结选项；由于第三轮失败，默认总结模型维持 0.8B。恢复默认值后仅做 Desktop Release 编译（0 警告、0 错误），未再运行媒体任务；不能把最终默认配置描述为通过新的端到端验收。
