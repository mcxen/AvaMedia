# 多阶段任务窗口截图

只用于多阶段任务窗口（UISPEC 10.16）评审，不运行回归测试。用 `DesignPersonClipTask` 的示意数据渲染保留有人片段窗口的配置、进行中、暂停、失败、完成和取消状态；画面帧来自指定的真实视频，片段、帧数与日志是标注过的示意数据，不是检测结果。

```sh
DISPLAY=:11 XDG_DATA_HOME=/tmp/avademo/data \
  AVAMEDIA_CAPTURE_FFMPEG=/tmp/ffmpeg-n9.0-latest-linux64-gpl-9.0/bin/ffmpeg \
  ~/.dotnet/dotnet run --project tools/AvaMedia.MultiStageCapture -p:EnableWindowsTargeting=true -- \
  <输出目录> <视频> Dark [Configuring Running Paused Failed Completed Cancelled]
```

截图依赖 `xdotool` 与 Python Pillow，按窗口原生框架裁剪。截图与媒体保留在仓库外。

## 其他工具

`tools` 模式用 `DesignToolTasks.cs` 中的示意数据渲染自动旋转、自动字幕、视频总结、自动标签分类与自动媒体整理窗口的六种状态（配置、进行中、暂停、失败、完成、取消），文件名为 `<工具>-<状态>-dark.png`；画面帧取自指定视频，方向采样帧为转置后的真实帧。`real-tool rotate|auto-subtitle` 用真实会话与 `QueueService` 运行一次，截取配置、进行中与最终状态（需要对应模型可用）。

```sh
DISPLAY=:11 XDG_DATA_HOME=<隔离数据目录> AVAMEDIA_CAPTURE_FFMPEG=<ffmpeg> \
  ~/.dotnet/dotnet run --project tools/AvaMedia.MultiStageCapture -- tools <输出目录> <视频> Dark [orientation subtitle summary tagging classification]
DISPLAY=:11 XDG_DATA_HOME=<隔离数据目录> AVAMEDIA_CAPTURE_FFMPEG=<ffmpeg> \
  ~/.dotnet/dotnet run --project tools/AvaMedia.MultiStageCapture -- real-tool rotate <输出目录> <视频>
```
