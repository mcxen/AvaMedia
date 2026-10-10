# 架构与接口层

`AvaMedia.Core` 不引用 Avalonia，负责模型、参数校验、任务计划、存储、媒体处理与方向识别。`AvaMedia.Desktop` 负责窗口、皮肤、预览呈现、交互与平台音频。

| 接口 | 职责 | 实际调用方 |
| --- | --- | --- |
| `IJobExecutor` | 任务执行、真实进度和取消 | `QueueService` |
| `IImageCompressor` | 静态图片探测、实际编码、真实体积与尺寸、停止和防覆盖 | 图片压缩窗口和队列执行器，共用 `ImageCompressor` |
| `IMediaPreview` | 缩略图、结束边界帧、相邻帧时间 | 编辑器，可独立注入帧预览实现 |
| `IMediaEngine` | 继承执行与预览契约，提供探测、设置与工具路径 | 主窗口、编辑器、转换、播放器和批量工具 |
| `IVideoOrientationDetector` | 方向建议、证据、进度与取消 | 编辑器方向页、批量旋转 |
| `ISummaryModel` | 本地／线上模型标识、后端、文本与多图请求、结构化输出和资源释放 | 视频总结、画面描述与 `MediaCaptionSession` |
| `ISummaryToolModel` | 带工具的多轮请求与实际工具结果 | 视频描述的指定时间取帧 |
| `IPlaybackSession` | 首帧、时钟、暂停 / 恢复、定位、速度、音量、呈现可见性与资源释放 | 独立播放器；可替换窗口的解码会话 |
| `IAudioOutput` | PCM 播放、暂停、音量、停止和资源释放 | `Playback`；桌面程序集内部的平台接口 |

生产实现为 `MediaEngine`。主窗口接受可选 `IMediaEngine`，其余业务窗口通过构造参数依赖接口；Windows 与 macOS 音频分别使用 WaveOut 和 AudioQueue。静态的参数校验、格式判断和文件命名仍作为纯业务规则，不增加转发接口。`PlayerWindow` 通过会话工厂使用 `IPlaybackSession`，生产会话 `Playback` 使用 FFmpeg 流式解码，也被剪辑器复用；无需让窗口依赖 FFmpeg 进程生命周期。实现与键位见 [播放器](PLAYER.md)。

AI 文本与视觉请求共用 `SummaryChatProtocol` 的图像内容和响应解析；`SummaryToolConversation` 管理多轮工具调用。`MediaCaptionSession` 在一次分析批次内复用同一个本地／线上模型，并在结束时释放；调用方借用模型时不负责销毁。实现的协议与真实模型对照见 [AI 复用消融](acceptance/ABLATION.md#2026-10-10-ai-代码复用消融)。

图片查看、普通静态图压缩、导出、效果和图片缩略图共用 `ImageCodec` 的本地编解码、方向与颜色处理和并发限制。`PreviewCacheStore` 统一派生图与任务摘要的内存 / 磁盘容量、完整性校验与原子写入；`TaskPreviewCache` 负责任务选轨 / 截取位置，普通静态图片行复用本地图像头读取，图片缩略图以源文件版本、成员与尺寸区分。普通图片压缩不依赖媒体子进程，HEIF 与高位深 PNG 保留专门路径；Skia 仍被 PDF、APNG 和 AI 图像处理使用，FFmpeg 仍被音视频及高位深图片使用。

Core 内部的 `HeifImage` 从 FFprobe 选择静态主图，识别完整 Tile Grid、方向、位深及单块派生图裁剪，统一供编码、转换与 PDF 使用。`AppleImageIO` 仅在 macOS 延迟加载系统框架，以 SafeHandle 管理原生资源，由 `ImageCodec` 在后台读取图片并按请求尺寸下采样；窗口不直接调用原生 API。图片压缩窗口只持有当前原图位图。详见 [HEIC](HEIC.md)。

`VideoFormats` 集中维护视频输入分类，播放器目录扫描、工具路由、剪辑、压缩、批量工具与 Windows 打开方式注册共用不可变集合；可原格式导出的容器单独维护，避免把输入支持当成输出支持。3GP / 3G2 的编码选择与校验沿用现有转换接口；Windows 安装与 macOS 构建验证共用 `scripts/legacy-video-capabilities.json` 声明的原生解码、解复用和输出能力。Mac 对应源码包包含该声明，可重建相同检查。见 [旧视频支持](LEGACY-VIDEO.md)。

界面采用 Avalonia 样式、语义资源与组件角色抽象：`UiStyles.axaml` 定义资源，`ControlRoles.axaml` 定义角色，`EditorStyles.axaml` 定义编辑器对齐，`Platinum*.axaml` 实现经典外观。窗口复用同一组角色；设计约束见 [UISPEC.MD](../UISPEC.MD)。

`PresentationVisible` 控制视频像素交付，隐藏 / 最小化时保持媒体时钟和音频，恢复窗口沿用当前解码会话。BGRA 使用池化帧缓冲，连续行跨度一次复制，非连续跨度按行处理；窗口的时间文字与滑块最多每秒刷新十次，画面按源帧率呈现。

剪辑器与天池播放器共用 `PreviewRequest` 的过期请求取消、`MediaFrameView` 的静态位图所有权及 `EditorTime` 的时间轴刷新节流。静态预览切换到流式帧时释放旧位图，播放会话继续拥有流式帧。编辑页与导出页的片段缩略图共用加载与并发限制。

`MediaEngine` 的逐帧浏览与剪辑终点预览共用真实时间戳查询。`MediaPreviewCache` 复用 `PreviewCacheStore` 保存原始 PNG 预览和时间戳；源路径、大小、修改时间、视频轨、尺寸、填充、定位方式及工具路径参与标识。时间戳窗口按整秒对齐，邻近定位复用已查询范围，媒体探测结果只在内存中限量保留。视频预览最多两个并发生成请求，同一缓存标识合并生成；缓存单项上限 8 MB，沿用总体 16 MB 内存 / 128 MB 磁盘容量。

`ProcessRunner.StartAsync` 把进程创建移出调用窗口线程，探测和缩略图从该入口启动；播放器已在后台的解码工作仍使用同步创建。`MediaEngine.Probe` 完整读取结构化 JSON，进程普通日志继续保留长度限制。

任务行通过 `MetadataReady` 跟踪后台文件大小读取，进度通知只更新状态字段。`Storage.SaveJobsAsync` 在后台序列化并原子替换队列文件，以递增版本阻止旧保存覆盖新编辑；主窗口退出等待最终 `PersistenceReady`。SHA256 工具通过 `FileHashing` 流式读取，池化 1 MiB 缓冲，并报告节流进度和取消。

Windows 播放器注册由 `SystemPlayerIntegration` 管理，通过当前用户的 Applications / ProgID / Capabilities 登记文件打开方式，激活命令统一为 `--play`；开始菜单与卸载调用由安装器配置。系统默认应用仍通过 Windows 设置选择。

`tests/AvaMedia.InterfaceTests` 无需 FFmpeg 或 UI 即可替换队列执行后端，验证并行任务、真实进度、完成事件、错误与取消。媒体、编辑器、方向和皮肤专项测试另外验证生产实现。
