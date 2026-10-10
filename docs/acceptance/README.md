# 验收与测量记录

每份记录只证明其注明的日期、提交、环境和样本范围。当前使用说明见 [文档索引](../README.md)，本目录保留历史证据。

## AI 与模型

| 日期 | 记录 | 范围 |
| --- | --- | --- |
| 2026-10-10 | [AI 任务端到端验收](AI-RUNTIME-ACCEPTANCE-20261010.md) | 真实队列、模型、画面描述、字幕、人物片段、转正及原生交互；含未通过的语义核对 |
| 2026-10-10 | [AI 代码复用消融](ABLATION.md#2026-10-10-ai-代码复用消融) | 四组源码、协议对照及本地／线上真实模型描述和工具调用 |
| 2026-10-09 | [文件夹分类](FOLDER-CLASSIFICATION-ACCEPTANCE.md) | 真实素材、分类、复制／移动与撤销 |
| 2026-10-09 | [真人标签](REAL-PEOPLE-TAGS-ACCEPTANCE.md) | 标签推理、描述与素材保护；准确性限定于样例 |
| 2026-10-09 | [中文标签与工作台](AI-TAG-CHINESE-ACCEPTANCE.md) | 中文候选、原生操作和导出 |
| 2026-10-09 | [AI 标签工作台](AI-TAG-WORKSPACE-ACCEPTANCE.md) | 工作台控件、真实推理与结果文件 |
| 2026-10-08 | [AI Beta 端到端](AI-END-TO-END.md) | 当日 JoyTag、旧语义后端、词库和人物片段；后端实现已更新，见 [模型管理](../MODELS.md) |
| 2026-10-08 | [有人片段原格式导出](PERSON-CLIP-ACCEPTANCE.md) | 实际源视频、区间、流复制与队列导出 |

## 界面、媒体与性能

| 日期 | 记录 | 范围 |
| --- | --- | --- |
| 2026-10-09 | [四套外观检查](APPEARANCE-REVIEW.md) | macOS 原生窗口及渲染记录 |
| 2026-10-09 | [视频操作界面重构](VIDEO-USABILITY.md) · [操作消融](VIDEO-USABILITY-ABLATION.md) | 字幕、AI 设置及结果交互 |
| 2026-10-09 | [工具边界审查](MEDIA-EDGE-CASE-AUDIT.md) | 工具入口与源码静态审查，未执行媒体输出回归 |
| 2026-10-08 | [VR 样片](PLAYER-VR-ACCEPTANCE.md) | macOS 原生播放器与公开 VR 样片 |
| 2026-10-08 | [WebView 嗅探](WEBVIEW-SNIFFING-CHECK-2026-10-08.md) | 四个公开页面及下载任务构造 |
| 2026-10-06 | [播放器性能](PLAYER-PERFORMANCE.md) | Windows 旧构建的采样数据；原始数据在 [measurements](../measurements/player-20261006.json) |
| 2026-10-06 | [旧 FFmpeg 体积比较](FFMPEG-SIZE.md) | 当日 BtbN LGPL 工具，当前发布配方见 [FFmpeg 构建](../FFMPEG-MACOS.md) |
| 2026-10-05 | [媒体处理消融](ABLATION.md#2026-10-05-windows-x64-实测) | Windows 转码、流复制、队列并行和预览 |

本机素材、模型及完整报告保存在各文档注明的 `artifacts/` 路径，未纳入 Git。历史记录中提到的实现和安装包不能直接作为当前版本的验证结论。
