# 视频总结 Agent Harness

调研日期：2026-10-13。目标是让本地与云端模型按需查看真实视频帧，避免固定画面描述与未经核实的转写主导总结。本轮范围为 Core 总结链路，不增加界面或改变用户选定的模型及供应商。

## 开源方案与选择

| 方案 | 官方资料中的能力 | 本次判断 |
| --- | --- | --- |
| [Pi agent-core](https://raw.githubusercontent.com/badlogic/pi-mono/main/packages/agent/README.md) | TypeScript 有状态工具循环、事件、上下文转换、工具执行钩子 | 借鉴循环与工具结果分离的设计；直接引入需要额外 JavaScript 运行时 |
| [smolagents](https://github.com/huggingface/smolagents) | Python 的精简 Agent，支持工具、视觉及多种模型 | 适合 Python 工程；本次无需引入 Python 执行环境或模型生成代码 |
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
- `record_observation`：记录实际已查看帧支持的观察；只有有效帧编号能进入总结证据，多帧动作观察需要近邻时间支持。

工具只能访问当前任务的视频和当前分析区间。模型不能选择其他文件、执行 shell 或访问任意 URL。运行时限制轮数、图片数量和上下文，允许一次结构纠正，保留工具执行记录；最终自由文本不会绕过证据链直接成为总结。工具真实错误和用户取消继续传播，决策协议失败保留明确说明及可用画面观察。

短视频调查结果进入现有 claim grounding。长视频在每个分析窗口完成视觉调查，再进入既有 map/reduce 与核验流程；检查点包含 Harness 版本，语音原文与用于总结的证据分开保存。本地视觉和文本模型顺序使用现有租约，避免为调查同时加载第二个本地模型。

`VideoSummaryTranscriptPolicy` 保留原始字幕导出，只从总结证据中排除明确非语句标记、病理重复及已有 VAD 明确不支持的转写。无 VAD 数据不视作无语音。Whisper 证据标记为 `speech_transcript`，事实提炼、归纳和核验保留语音来源及不确定性。零散或无关的识别语句不应覆盖画面的场景、主体和主要活动；正常连贯的教学或叙述语音仍可总结。

## 能力边界与检查范围

Harness 提供重新观察和纠错的机会，不改变模型权重，也不能保证小模型的视觉语义或转写准确。当前未新增音频语义分类器，不能仅凭 VAD 判断音乐、广播或对话；因此不会声称已经可靠识别并剔除所有环境音幻觉。

按 `AGENTS.md`，本轮只运行受影响项目编译与必要静态诊断，不新增测试、不运行媒体、模型、界面或性能验收。用户提到的商场视频尚未提供，不能据此宣称该视频的总结质量已通过验收。

本地检查：`AvaMedia.Core` Release 构建通过，0 警告、0 错误；修改范围的空白错误检查通过。长链分段系统提示词与 schema 静态计数约 715 tokens，低于现有 1024 预留。Agent 的上下文边界另按字符估算，不等同于模型的精确 token 计数；传入图像复用现有 384 像素预处理，完整 PNG 单独保存，执行记录不内嵌图片数据。
