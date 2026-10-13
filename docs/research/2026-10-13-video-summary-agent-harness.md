# 视频总结 Agent Harness

调研日期：2026-10-13。目标是让本地与云端模型按需查看真实视频帧，避免固定画面描述与未经核实的转写主导总结。本轮范围为 Core 总结链路，不增加界面或改变用户选定的模型及供应商。

## 开源方案与选择

| 方案 | 官方资料中的能力 | 本次判断 |
| --- | --- | --- |
| [Pi agent-core](https://raw.githubusercontent.com/badlogic/pi-mono/main/packages/agent/README.md) | TypeScript 有状态工具循环、事件、上下文转换、工具执行钩子 | 借鉴循环与工具结果分离的设计；直接引入需要额外 JavaScript 运行时 |
| [smolagents](https://huggingface.co/docs/smolagents/en/reference/agents) | ReAct 行动/观察循环、`planning_interval`、`final_answer_checks` 和最大步数 | 借鉴阶段计划与结束检查；无需引入 Python 执行环境或模型生成代码 |
| [Deep Agents](https://docs.langchain.com/oss/python/deepagents/overview) | `TodoListMiddleware` / `write_todos` 维护任务状态，基于 LangGraph 执行；当前文档的 v0.7 起计划能力按需启用 | 借鉴未完成事项的显式状态；本次不增加另一套运行时 |
| [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) | .NET/Python Agent 与多 Agent 工作流 | 完整框架覆盖本次不需要的编排场景 |
| [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) | IChatClient、多模态内容、自动函数调用及中间件 | 选择其工具循环作为轻量基础，复用现有模型与媒体实现 |

实际依赖固定为 [Microsoft.Extensions.AI 10.10.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0)，该版本提供 net8.0 目标。代码真正使用 `FunctionInvokingChatClient` 和 `AIFunction` 执行工具；Pi 与 smolagents 只是设计参考，没有作为依赖或复制其源码。

## 原有断点

- `SummaryToolConversation` 和 `MediaCaptionFrameTool` 已用于画面描述功能，但视频总结没有接入。
- 短视频固定逐帧和近邻比较后，文本模型只能阅读描述；长视频同样先固定描述，再执行分段归纳。
- Whisper 结果直接成为 `transcript`，未保留与外部/内嵌字幕的可信度差别。VAD 只表明存在类似语音的声音，语言检测置信度不能证明转写正确。

## 本轮实现

`SummaryAgentHarness` 把现有 `ISummaryModel.CompleteAsync` 适配为 `IChatClient`。视觉模型每轮输出受 JSON schema 约束的单步决策，框架执行白名单工具，再将工具文本及实际 PNG 回送给模型。这不要求本地模型原生支持 `tool_calls`，也继续使用现有线上 Chat Completions 供应商配置。

`VideoSummaryFrameAgent` 先提供覆盖当前区间的有序总览，再允许模型调用：

- `get_video_frames`：指定时间取帧或局部放大；复用现有媒体引擎，返回实际帧时间与 PNG。
- `read_transcript`：读取当前区间内相关时间段的文字及来源提示。
- `record_observation`：用单个 `frameId` 记录静态可见事实，不让模型额外判断布尔类型的动作标记。
- `record_sequence_observation`：引用至少两张近邻画面记录连续动作；相邻时间必须不同且不超过 5 秒。
- `mark_frame_uncertain`：对无法可靠描述的已查看画面留下具体不确定性，不生成事实。

工具只能访问当前任务的视频和当前分析区间。模型不能选择其他文件、执行 shell 或访问任意 URL。运行时限制轮数、图片数量和上下文，保留工具执行记录；最终自由文本不会绕过证据链直接成为总结。工具真实错误和用户取消继续传播，决策协议失败保留明确说明及可用画面观察。

工具决策使用带实际参数对象的联合 schema，而不是让小模型在字符串中再生成转义 JSON。完整画面重复请求会返回缓存 PNG 和原编号，不消耗新帧预算。未保存任何观察，或总览图仍未记录观察/说明不确定性，不能以 `finish` 宣称完成；可以用 `abstain` 说明无法确认。记录请求、参数拒绝和后备描述分别留痕，后备观察不能视为自主工具调用成功。视觉资料不能支撑音乐、配乐等声音推断，相关候选须有文字来源并经过原资料核验。

短视频调查结果进入现有 claim grounding。长视频在每个分析窗口完成视觉调查，再进入既有 map/reduce 与核验流程；检查点包含 Harness 版本，语音原文与用于总结的证据分开保存。本地视觉和文本模型顺序使用现有租约，避免为调查同时加载第二个本地模型。

本地 `LocalSummaryModelId` 可显式设为 `summary-qwen35-abliterated`，由 Qwen3.5 4B 做最终归纳和引用复核。视觉模型可以处理纯文字请求，无需固定将 4B 的观察交给 0.8B 总结。长视频 4B 预算对应本机实际 16K 上下文，视觉阶段结束后再获取总结租约；报告保存实际模型 ID。桌面预热沿用实际所选模型并去重，选择 4B 总结时不额外预热 0.8B。第三轮全 4B 实测未生成通过证据校验的章节，因此保留现有 0.8B 默认值；可选 4B 的接入已完成，但不能宣称该配置通过语义验收。

[Qwen3.5 4B 官方资料](https://huggingface.co/Qwen/Qwen3.5-4B)将模型定义为带视觉编码器的语言模型，并公布语言与视觉指标；[0.8B 官方资料](https://huggingface.co/Qwen/Qwen3.5-0.8B)提供同系列小模型指标。官方模型成绩不能直接替代本项目所用量化衍生模型、禁用思考模式下的实测；本次改路由和工具接口后复测属于整条链路验证，不是控制变量的模型能力基准。

`VideoSummaryTranscriptPolicy` 保留原始字幕导出，只从总结证据中排除明确非语句标记、病理重复及已有 VAD 明确不支持的转写。无 VAD 数据不视作无语音。Whisper 证据标记为 `speech_transcript`，事实提炼、归纳和核验保留语音来源及不确定性。零散或无关的识别语句不应覆盖画面的场景、主体和主要活动；正常连贯的教学或叙述语音仍可总结。

## 能力边界与检查范围

Harness 提供重新观察和纠错的机会，不改变模型权重，也不能保证小模型的视觉语义或转写准确。当前未新增音频语义分类器，不能仅凭 VAD 判断音乐、广播或对话；因此不会声称已经可靠识别并剔除所有环境音幻觉。

初始实现按 `AGENTS.md` 只运行受影响项目编译与必要静态诊断。用户随后明确要求 GPT 6 luna 用 MCP 下载视频并验证本地模型，因此追加同一条 69.7 秒街头舞蹈视频的定向端到端验证，失败与复测保留在[验收报告](../acceptance/VIDEO-SUMMARY-AGENT-LOCAL-20261013.md)。不新增自动测试或扩展界面、性能验收。用户提到的商场闲逛视频尚未提供，不能据此宣称该视频的总结质量已通过验收。

本地检查：`AvaMedia.Core` Release 构建通过，0 警告、0 错误；修改范围的空白错误检查通过。长链分段系统提示词与 schema 静态计数约 715 tokens，低于现有 1024 预留。Agent 的上下文边界另按字符估算，不等同于模型的精确 token 计数；传入图像复用现有 384 像素预处理，完整 PNG 单独保存，执行记录不内嵌图片数据。

用户随后要求云端验证，使用已配置的 Dots 模型对同一素材运行两次：首轮供应商超时；重试完成两次自主补帧和两次观察记录，但在最终归纳阶段遇到无效 JSON。详情见[云端验收报告](../acceptance/VIDEO-SUMMARY-AGENT-CLOUD-20261013.md)。这证明按需截图工具已实际使用，不能视为完整总结验收通过。实测还发现 MCP 部分结果未同步磁盘中的调用记录，已在 SaveInvestigation 中同步 AgentTrace；该诊断修复只进行相关编译检查，未新增云端运行。

## 计划、核验与纠正循环补齐

根据本地遗漏结尾、重复观察，以及云端 JSON 解析失败的证据，进一步参考 Pi 的有状态行动/结果消息与完成钩子、smolagents 的计划/最终检查、Deep Agents 的任务状态，补齐共享执行链：

1. 每个工具决策携带简短 `plan` 和 `nextCheck`（各不超过 240 字），说明待查事项及可观察的检查目标。它们属于公开行动记录，不作为事实来源，也不要求或保存模型隐藏推理。
2. 每轮注入实际状态：已看画面编号、实际时间、是否裁剪、是否保存观察/不确定性、待核实总览图以及剩余预算。状态从真实执行结果计算，模型不能靠把计划写成“完成”来修改。
3. 默认最多 8 轮决策、7 次工具执行、10 次模型请求；决策格式最多纠正 2 次。工具失败、参数错误、返回结果和结束拒绝都进入有界上下文。小模型上下文优先保留完成条件和当前状态，压缩旧历史；磁盘执行记录保留原始步骤，不保存 PNG 的内嵌数据。
4. 初始总览图逐张有观察或明确不确定性，且至少存在一条观察，才能结束。结束检查失败给出具体缺失编号，预算允许时继续调查；预算不足时须明确放弃。可重新附上旧完整截图，裁剪另算新帧；完全相同的观察复用原编号，避免重复增加证据。
5. 内容表述、逐条 `supported` 审核、总结结构先检查完整 JSON 及当前 schema 的字段/类型/数量/长度。格式不合格时，同一模型按原资料、原任务、schema 和具体格式错误完整重生成一次。结构通过后才进入引用与语义审核，纠正不能跳过原审核；再次格式错误、拒绝、超时、IO 和取消均明确失败。

本次未更改本地模型原生 thinking 开关、权重、默认模型或云端供应商。公开计划和证据核验循环可以为模型提供纠错机会，但不代表更换为更强的模型，也不保证错误视觉描述会自动被识别。Harness 版本升至 5，长视频检查点据此失效重算。

这一轮在基于 `origin/main` 的隔离工作区完成 Core Release 构建，0 警告、0 错误，并通过本次修改的空白错误检查。未重跑媒体或云端推理。上方历史验收结论保持不变；新增循环能否改善同一素材的最终总结，尚未经端到端复测。
