# 文档索引

功能与限制见 [功能说明](FEATURES.md)。日常开发遵循 [AGENTS.md](../AGENTS.md)、[Git 工作流](GIT-WORKFLOW.md) 和 [界面规范](../UISPEC.MD)。

## 使用与配置

| 主题 | 文档 |
| --- | --- |
| 安装、平台、设置 | [Windows 与 macOS](PLATFORMS.md) · [首次使用](FIRST-RUN-SETUP.md) · [选项设置](SETTINGS.md) · [普通选项](GENERAL-OPTIONS.md) |
| 任务与结果 | [任务管理](TASK-MANAGEMENT.md) · [后台任务与托盘](BACKGROUND-TASKS.md) · [通知](NOTIFICATIONS.md) · [剩余时间](PROGRESS-ESTIMATE.md) · [文件路由](MEDIA-ROUTING.md) |
| 转换与压缩 | [视频压缩](VIDEO-COMPRESSION.md) · [视频瘦身](VIDEO-SLIMMING.md) · [图片压缩](IMAGE-COMPRESSION.md) · [GPU 转码](GPU-TRANSCODING.md) · [HEIC](HEIC.md) · [TS 视频](TS-VIDEO.md) · [旧视频格式](LEGACY-VIDEO.md) |
| 剪辑与导出 | [快速剪辑](QUICK-CLIP.md) · [视频编辑](VIDEO-EDITING.md) · [批量裁剪](BATCH-CROP.md) · [批量旋转](BATCH-ROTATE.md) · [原属性导出](SOURCE-VIDEO-EXPORT.md) · [音频配置](AUDIO-OPTIONS.md) · [字幕与选轨](SUBTITLE-OPTIONS.md) |
| 播放与看图 | [天池播放器](PLAYER.md) · [原生播放](PLAYER-NATIVE.md) · [VR 视频](PLAYER-VR.md) · [天池看图](IMAGE-VIEWER.md) |
| AI 与模型 | [AI 标签工作台](MEDIA-AI.md) · [NSFW 标签](NSFW-REVIEW.md) · [自动媒体整理](FOLDER-CLASSIFICATION.md) · [视频总结与画面描述](VIDEO-SUMMARY.md) · [保留有人片段](PERSON-CLIP.md) · [自动字幕与人声增强](SPEECH-TOOLS.md) · [词库](WORD-LIBRARIES.md) · [模型管理](MODELS.md) · [模型安装](MODEL-INSTALLATION.md) |
| 文件与文档 | [视频下载](VIDEO-DOWNLOAD.md) · [WiFi 传文件](WIFI-TRANSFER.md) · [PDF 工作区](PDF-WORKSPACE.md) · [批量工具](BATCH-TOOLS.md) |
| 外观 | [Mac OS 9](MACOS9-SKIN.md) · [Windows XP](WINDOWS-XP-SKIN.md) · [文档图标](DOCUMENT-ICONS.md) · [界面截图](assets/screenshots/README.md) |

## 开发与发布

- [架构与接口](ARCHITECTURE.md)、[MCP 服务](MCP.md)、[二级工具界面](TOOL-INTERFACES.md)、[空闲内存](IDLE-MEMORY.md)。
- [自动发布与打包](RELEASE.md)、[macOS FFmpeg 配方](FFMPEG-MACOS.md)、[第三方声明](../THIRD-PARTY-NOTICES.md)。
- [验收与测量记录](acceptance/README.md)：按日期和范围查看真实模型、媒体输出、界面、消融与性能证据。
- [版本记录](releases/README.md)：发布说明、历史变更和已归档的校验数据。

## 本地开发资料

`artifacts/` 保存验收报告、素材、源码快照、补丁及临时构建；`.tools/` 保存开发依赖。两者均被 Git 忽略。验收文档中的本地证据路径只在保存该资料的开发机器上可用；构建输出清理后需重新编译才能复现。

清理仅删除确认可再生成且未被使用的 `bin/`、`obj/`、缓存和网站构建输出。保留模型、依赖、原始素材、报告、源码快照与正在使用的目录。功能验证的执行范围以用户要求及 `AGENTS.md` 为准。
