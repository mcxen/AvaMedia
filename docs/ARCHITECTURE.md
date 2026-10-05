# 架构与接口层

`AvaMedia.Core` 不引用 Avalonia，负责模型、参数校验、任务计划、存储、媒体处理与方向识别。`AvaMedia.Desktop` 负责窗口、皮肤、预览呈现、交互与平台音频。

| 接口 | 职责 | 实际调用方 |
| --- | --- | --- |
| `IJobExecutor` | 任务执行、真实进度和取消 | `QueueService` |
| `IMediaPreview` | 缩略图、结束边界帧、相邻帧时间 | 编辑器，可独立注入帧预览实现 |
| `IMediaEngine` | 继承执行与预览契约，提供探测、设置与工具路径 | 主窗口、编辑器、转换、播放器和批量工具 |
| `IVideoOrientationDetector` | 方向建议、证据、进度与取消 | 编辑器方向页、批量旋转 |
| `IAudioOutput` | 播放、音量、停止和资源释放 | `Playback`；桌面程序集内部的平台接口 |

生产实现为 `MediaEngine`。主窗口接受可选 `IMediaEngine`，其余业务窗口通过构造参数依赖接口；Windows 与 macOS 音频分别使用 WaveOut 和 AudioQueue。静态的参数校验、格式判断和文件命名仍作为纯业务规则，不增加转发接口。当前播放器使用 FFmpeg 进程解码；替换解码技术时需要同时替换 `Playback` 实现。

界面采用 Avalonia 样式、语义资源与组件角色抽象：`UiStyles.axaml` 定义资源，`ControlRoles.axaml` 定义角色，`EditorStyles.axaml` 定义编辑器对齐，`Platinum*.axaml` 实现经典外观。窗口复用同一组角色；设计约束见 [UISPEC.MD](../UISPEC.MD)。

`tests/AvaMedia.InterfaceTests` 无需 FFmpeg 或 UI 即可替换队列执行后端，验证并行任务、真实进度、完成事件、错误与取消。媒体、编辑器、方向和皮肤专项测试另外验证生产实现。
