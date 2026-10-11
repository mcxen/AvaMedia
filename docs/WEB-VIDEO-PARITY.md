# 网页视频工作区与客户端对齐

目标：网页可视化操作完整对应客户端全部视频工具。以 `Catalog.All` 的视频分类及批量裁剪、批量旋转、多宫格截图为入口范围，以各桌面工作区的配置、预览、提交、结果复核流程为行为依据。滑块、拖框、预设、文件树替代参数手填；共享队列、服务和媒体计划，不另写输出算法。

**尚未完成整体对齐。** 下表用于追踪缺口，不能把任务可创建或编译成功当作界面、媒体输出验收。已有网页历史验收见 [WEB-CONSOLE](acceptance/WEB-CONSOLE.md)，不自动覆盖后续修改。

## 已接入的共享实现

- `AvaMedia.MediaUi` 提供客户端与 Browser 共用的裁剪框、双端区间条和片段时间线。桌面 XP 的区间条绘制仍由桌面外观层处理，拖动逻辑共用。裁剪框支持四角缩放、移动、比例、方向键、Shift 大步移动与 Esc 撤销当前手势。
- 快速剪辑网页草稿按文件和片段保存，支持新增、复制、删除、排序、当前位置分割、反选去除、独立导出与按源文件合并。`avamedia_export_clips` 复用 `QuickClipWorkflow`、`QuickClipBatch`；提交前核对预览时的源文件版本，沿用持久提交 ID 恢复机制。
- 网页轨道选择读取真实媒体轨道，混流分别读取第一个文件的视频轨和第二个文件的音频轨。媒体信息入口展示实际体积、时长、尺寸和轨道。
- 视频压缩预估使用 `VideoCompression.Plan`，质量模式不虚构体积；参数按模式显示，自动档隐藏由档位决定的字段。字幕和编码参数也按适用条件显示。
- 预览继续单帧按需加载、取消过期请求、限制尺寸与并发，不批量装入所有视频内容。

## 完成审计清单

| 工具 | 桌面依据 | 尚需完成或证明的流程 |
| --- | --- | --- |
| 格式转换 | ConvertWindow、OptionsWindow | 编码/格式联动、轨道、字幕样式、批量来源与固定输出区完整对齐及验收 |
| 视频压缩 | VideoCompressionWindow | 逐文件计划/错误列表、颜色/HDR 提示、估算与执行对应；新表单和预估待验收 |
| 视频瘦身 | VideoSlimmingWindow | 分析、样片与质量/体积对比、分析结果复用和接受后导出 |
| 视频合并 | ConvertWindow、OptionsWindow | 可视排序、逐源信息/错误、导出流程对应及验收 |
| 提取音频 | ConvertWindow、OptionsWindow | 真实轨道选择、音频配置和区间完整对应及验收 |
| 快速剪辑 | QuickClipWorkspaceWindow、EditorWindow、ClipExportWindow | 批量分割、预览结果、文件勾选/就绪状态与导出页对齐；新多片段流程待浏览器验收 |
| 保留有人片段 | PersonClipWindow | 分析证据、候选片段复核、选取/排除、导出与部分结果 |
| 去除水印 | EditorWindow | 客户端去水印模式、模型配置与预览/处理路径核对及验收 |
| 导出帧 | ConvertWindow | 帧格式、区间、采样和输出列表对应及验收 |
| 播放器 | PlayerWindow | 网页内播放工作区、媒体/字幕/轨道操作；核对服务器可供浏览器播放的路径 |
| 视频下载 | DownloadWindow | 链接分析、格式/清晰度/字幕选择、批量条目与下载任务 |
| 自动字幕 | SpeechToolsWindow、SubtitleReviewWindow | 识别结果校对、时间轴、字幕样式和二次导出 |
| 视频总结 | VideoSummaryWindow、VideoSummaryResultWindow | 配置项、证据/章节/字幕分视图、部分结果与导出 |
| 人声增强 | VoicePreviewWindow、SpeechToolsWindow | 增强前后试听、模型状态、音频参数与导出 |
| 视频/音频混流 | ConvertWindow、OptionsWindow | 两个来源的轨道选择已接入，来源角色/顺序及运行待验收 |
| 提取视频流 | ConvertWindow | 真实视频轨、容器与流复制行为对应及验收 |
| 媒体信息 | MediaInfoWindow | 概览与真实轨道已接入；完整分组信息及浏览器验收 |
| 修复/重新封装 | ConvertWindow | 可保留轨道/容器设置、结果与限制提示对应及验收 |
| 批量裁剪 | BatchCropWindow | 已共用选区控件；像素/相对比例应用、逐文件验证、来源勾选及验收 |
| 批量旋转 | BatchRotateWindow、RotationTaskView | 各文件方向、自动识别/复核、原编码/快速旋转模式及验收 |
| 多宫格截图 | ContactSheetWindow | 网格示意、每视频多张、时间戳、区间、单格尺寸与完整任务选项 |

还需核对统一布局（来源、预览、参数与固定输出操作）、任务详情中上述全部结果的可视阅读/后续操作，以及宽窄窗口可用性。完成这些项后才可声称整体对齐；当前检查按 AGENTS.md 执行相关项目编译和必要静态诊断。
