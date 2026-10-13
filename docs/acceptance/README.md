# 验收与测量记录

每份记录只证明其注明的日期、提交、环境和样本范围。当前使用说明见 [文档索引](../README.md)，本目录保留历史证据。

## AI 与模型

| 日期 | 记录 | 范围 |
| --- | --- | --- |
| 2026-10-11 | [Qwen3.5 识别与总结](QWEN35-NSFW-RECOGNITION-20261011.md) | 生产队列与指定视频；包含识别可靠性未通过及速度下降的结论 |
| 2026-10-11 | [相似服装指定配对](OUTFIT-CONTEXT-3576-3577-20261011.md) | 六段用户视频与指定两段的归组；不代表全库准确率 |
| 2026-10-11 | [分类链路精简](classification-simplification-20261011.md) | 指定基线下的源码、结果和消融对照 |
| 2026-10-10 | [本地模型预热速度](MODEL-WARMUP-SPEED-20261010.md) | Apple M4、当时两个默认模型；文本模型后续已更换 |
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
| 2026-10-13 | [顶层窗口与独立窗口](WINDOW-CHROME-20261013.md) | 所记窗口框架提交、平台与交互范围 |
| 2026-10-11 | [IMG_4440 自动转正](ORIENTATION-IMG4440.md) | 指定源视频的方向建议、预览及完整导出 |
| 2026-10-11 | [天池看图打开速度](IMAGE-VIEWER-SPEED-20261011.md) | macOS 原生首帧、20,001 项目录、实际封面双击及静态显示帧压缩修复 |
| 2026-10-10 | [设置与输出配置走读](SETTINGS-WALKTHROUGH-20261010.md) | 当日设置布局；当前分类以 [设置说明](../SETTINGS.md) 为准 |
| 2026-10-10 | [自动字幕界面走读](SUBTITLE-UI-WALKTHROUGH-20261010.md) | 生产字幕提交、进度、校对与样式控件的原生走读 |
| 2026-10-10 | [本地界面走查](UI-LAYOUT-WALKTHROUGH-20261010.md) | 原生窗口、部分真实后端及受控队列进度；不证明真实总结或下载结果 |
| 2026-10-10 | [媒体复用端到端](media-preview-20261010.md) | 任务封面、共享图片编解码、剪辑浏览与播放器 |
| 2026-10-09 | [四套外观检查](APPEARANCE-REVIEW.md) | macOS 原生窗口及渲染记录 |
| 2026-10-09 | [视频操作界面重构](VIDEO-USABILITY.md) · [操作消融](VIDEO-USABILITY-ABLATION.md) | 字幕、AI 设置及结果交互 |
| 2026-10-09 | [工具边界审查](MEDIA-EDGE-CASE-AUDIT.md) | 工具入口与源码静态审查，未执行媒体输出回归 |
| 2026-10-08 | [VR 样片](PLAYER-VR-ACCEPTANCE.md) | macOS 原生播放器与公开 VR 样片 |
| 2026-10-08 | [WebView 嗅探](WEBVIEW-SNIFFING-CHECK-2026-10-08.md) | 四个公开页面及下载任务构造 |
| 2026-10-06 | [播放器性能](PLAYER-PERFORMANCE.md) | Windows 旧构建的采样数据；原始数据在 [measurements](../measurements/player-20261006.json) |
| 2026-10-06 | [旧 FFmpeg 体积比较](FFMPEG-SIZE.md) | 当日 BtbN LGPL 工具，当前发布配方见 [FFmpeg 构建](../FFMPEG-MACOS.md) |
| 2026-10-05 | [媒体处理消融](ABLATION.md#2026-10-05-windows-x64-实测) | Windows 转码、流复制、队列并行和预览 |

## 远程网页

[网页端到端验证](WEB-CONSOLE.md) 分别记录旧 HTML 界面和后续 Avalonia Browser 的操作与媒体任务范围；当前实现及未完成事项见 [网页视频功能对齐状态](../WEB-VIDEO-PARITY.md)。

本机素材、模型及完整报告保存在各文档注明的 `artifacts/` 路径，未纳入 Git。历史记录中提到的实现和安装包不能直接作为当前版本的验证结论。
