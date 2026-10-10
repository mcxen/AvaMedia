# 功能与限制

本页按当前 `Catalog.All` 和功能实现整理。操作步骤见对应文档；日期、样本及已验证范围见 [验收记录](acceptance/README.md)。

| 类别 | 当前功能 | 说明与限制 |
| --- | --- | --- |
| 转换 | 视频／音频／图片转换，合并、混流、流提取、重新封装、媒体信息、导出帧 | 编码与硬件能力取决于所用 FFmpeg；见 [音频配置](AUDIO-OPTIONS.md)、[GPU 转码](GPU-TRANSCODING.md)、[TS](TS-VIDEO.md)、[旧视频格式](LEGACY-VIDEO.md) |
| 视频压缩与瘦身 | 质量、码率与目标体积压缩；分析画面后瘦身 | 质量模式不保证体积；瘦身不能还原历史原片，见 [视频压缩](VIDEO-COMPRESSION.md)、[视频瘦身](VIDEO-SLIMMING.md) |
| 视频编辑 | 多片段剪辑、时间区间、裁剪、旋转、镜像、速度、淡入淡出与去水印 | Fast Copy 切点受关键帧限制；去水印使用矩形插值。见 [快速剪辑](QUICK-CLIP.md)、[视频编辑](VIDEO-EDITING.md) |
| 批量视频 | 共用／逐文件裁剪和旋转，离线人脸方向建议，保留源容器及属性导出 | 人脸不清晰时需确认；画面滤镜需要重新编码。见 [批量裁剪](BATCH-CROP.md)、[批量旋转](BATCH-ROTATE.md)、[原属性导出](SOURCE-VIDEO-EXPORT.md) |
| 字幕与语音 | 指定音视频轨、字幕轨与烧录样式；离线自动字幕、人工校对、人声增强 | 位图字幕烧录及独立字幕轨拼接有限制；见 [字幕与选轨](SUBTITLE-OPTIONS.md)、[语音工具](SPEECH-TOOLS.md) |
| 播放 | 天池播放器、播放列表、选轨、字幕、快捷键、原生 mpv、VR 视频 | 原生播放依赖可选运行时；见 [播放器](PLAYER.md)、[原生播放](PLAYER-NATIVE.md)、[VR](PLAYER-VR.md) |
| 图片 | 格式转换、裁剪／缩放／旋转、压缩、HEIC／HEIF、天池看图 | 看图支持 ZIP／RAR／7z 内图片；通用归档工具仍限 ZIP。见 [图片压缩](IMAGE-COMPRESSION.md)、[HEIC](HEIC.md)、[天池看图](IMAGE-VIEWER.md) |
| PDF | 可视化选页、排序、旋转、合并、拆分、文字提取、图片／TXT 转 PDF、做旧与压缩 | DOCX／XLSX 导出提取文字，不恢复原排版；部分处理会栅格化，见 [PDF 工作区](PDF-WORKSPACE.md) |
| 下载与传输 | yt-dlp 批量解析及下载、内嵌 WebView 嗅探、登录态、代理、续传；局域网 WiFi 收发 | 网站登录和资源限制依实际站点而定；见 [视频下载](VIDEO-DOWNLOAD.md)、[WiFi 传文件](WIFI-TRANSFER.md) |
| 文件工具 | 批量重命名、预览与撤销、多宫格截图、ZIP 压缩／解压、DVD／VOB 合并转换、光盘数据复制 ISO | ISO 数据复制不等同于音乐 CD 抓轨；见 [批量工具](BATCH-TOOLS.md)、[文件路由](MEDIA-ROUTING.md) |
| AI 标签 | 图片／视频标签、中文词库、NSFW 复核、画面描述、结果查看与导出 | 有限采样可能漏掉内容，模型分数和描述不保证事实准确性；见 [标签工作台](MEDIA-AI.md)、[NSFW](NSFW-REVIEW.md)、[词库](WORD-LIBRARIES.md) |
| 自动媒体整理 | 按标签、语义、时长、姿势、人物、相似服装等分组，手工调整、复制／移动与撤销 | 分组准确性取决于素材和模型；见 [自动媒体整理](FOLDER-CLASSIFICATION.md) |
| 视频总结 | 本地／线上视觉与文本模型、语音识别、采样描述、补帧工具及章节报告 | 本地模型按需安装；线上请求发送选定内容；见 [视频总结](VIDEO-SUMMARY.md)、[模型管理](MODELS.md)、[模型安装](MODEL-INSTALLATION.md) |
| 有人片段 · Beta | 多模型人物检测、排除区间、结果复核与原格式／重新编码导出 | 小人物、遮挡、低光及有限采样可能误判；见 [保留有人片段](PERSON-CLIP.md) |
| 任务与集成 | 共享队列、并发、进度、停止、重试、参数编辑、结果恢复、后台窗口、通知及 MCP | 操作按任务状态开放；见 [任务管理](TASK-MANAGEMENT.md)、[后台任务](BACKGROUND-TASKS.md)、[通知](NOTIFICATIONS.md)、[MCP](MCP.md) |
| 平台与外观 | Windows x64、macOS ARM64；中英文、浅／深色、Mac OS 9、Windows XP 皮肤 | 平台要求与安装说明见 [平台](PLATFORMS.md)；设计见 [界面规范](../UISPEC.MD) |

LaMa 目前只有模型安装入口，尚未接入修复推理。OCR、PDF 原排版恢复、音乐 CD 抓轨和 ISO／CSO 互转未实现。屏幕录制和独立文件校验工具已移除。

历史构建、测试数量和旧包检查保存在 [验收目录](acceptance/README.md) 与 [版本目录](releases/README.md)，对应的通过记录仅适用于所记提交、平台和样本。当前开发检查范围遵循 [AGENTS.md](../AGENTS.md)。
