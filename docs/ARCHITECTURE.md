# 架构与接口层

`AvaMedia.Core` 不引用 Avalonia，负责模型、参数校验、任务计划、存储、媒体处理与方向识别。`AvaMedia.Desktop` 负责窗口、皮肤、预览呈现、交互与平台音频。

| 接口 | 职责 | 实际调用方 |
| --- | --- | --- |
| `IJobExecutor` | 任务执行、真实进度和取消 | `QueueService` |
| `IImageCompressor` | 静态图片探测、实际编码、真实体积与尺寸、停止和防覆盖 | 图片压缩窗口和队列执行器，共用 `FfmpegImageCompressor` |
| `IMediaPreview` | 缩略图、结束边界帧、相邻帧时间 | 编辑器，可独立注入帧预览实现 |
| `IMediaEngine` | 继承执行与预览契约，提供探测、设置与工具路径 | 主窗口、编辑器、转换、播放器和批量工具 |
| `IVideoOrientationDetector` | 方向建议、证据、进度与取消 | 编辑器方向页、批量旋转 |
| `IPlaybackSession` | 首帧、时钟、暂停 / 恢复、定位、速度、音量与资源释放 | 独立播放器；可替换窗口的解码会话 |
| `IAudioOutput` | PCM 播放、暂停、音量、停止和资源释放 | `Playback`；桌面程序集内部的平台接口 |

生产实现为 `MediaEngine`。主窗口接受可选 `IMediaEngine`，其余业务窗口通过构造参数依赖接口；Windows 与 macOS 音频分别使用 WaveOut 和 AudioQueue。静态的参数校验、格式判断和文件命名仍作为纯业务规则，不增加转发接口。`PlayerWindow` 通过会话工厂使用 `IPlaybackSession`，生产会话 `Playback` 使用 FFmpeg 流式解码，也被剪辑器复用；无需让窗口依赖 FFmpeg 进程生命周期。实现与键位见 [播放器](PLAYER.md)。

界面采用 Avalonia 样式、语义资源与组件角色抽象：`UiStyles.axaml` 定义资源，`ControlRoles.axaml` 定义角色，`EditorStyles.axaml` 定义编辑器对齐，`Platinum*.axaml` 实现经典外观。窗口复用同一组角色；设计约束见 [UISPEC.MD](../UISPEC.MD)。

`tests/AvaMedia.InterfaceTests` 无需 FFmpeg 或 UI 即可替换队列执行后端，验证并行任务、真实进度、完成事件、错误与取消。媒体、编辑器、方向和皮肤专项测试另外验证生产实现。
