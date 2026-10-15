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

## 调整片段（方案 A）

`adjust <PNG 路径> [default|drag|hover|menu|stale]` 固定使用 Avalonia Headless、深色主题、zh-CN、1240×800 客户区，不连接 X 显示、不移动或激活其它应用窗口。弹出菜单使用 overlay popup，因此会进入同一张图。PNG 不含系统标题栏。

依赖 .NET 8 SDK、带 `lavfi`/`testsrc2` 和 `libx264` 的 FFmpeg，以及 FFprobe；默认从 PATH 查找，可通过 `AVAMEDIA_CAPTURE_FFMPEG` / `AVAMEDIA_CAPTURE_FFPROBE` 指定路径。不需要安装人物检测模型。省略状态时使用 `default`；无效状态或参数数量返回退出码 2，生成失败返回 1。

每次运行在 `/tmp/avamedia-adjust-capture-*` 生成一段 640×360、15 fps 的合成测试视频，构造已完成的检测 Job（示意分数、黑灯、排除区间、不确定样本及低置信度段），**真实调用 `PersonClipEditing.OpenAsync`**，再通过会话命令收紧一个起点、移除一段、手动新增一段。图上的「截图夹具」说明这些分数并非实际模型推断；仅画面与编辑会话使用真实引擎和实现。

`drag` 用 Headless 指针输入拖动选中段的**终点**，停在实际 `Snap` 给出的检测边界，保留拖动中状态；`hover` 将指针移入预览；`menu` 点击检查器「更多」；`stale` 修改生成视频的写入时间。契约没有逐帧人物框，`hover` 不会伪造框或显示检测框开关。

```sh
~/.dotnet/dotnet build tools/AvaMedia.MultiStageCapture -p:EnableWindowsTargeting=true

# 只生成需要审阅的状态；无需 DISPLAY / xvfb / xdotool / Pillow。
XDG_DATA_HOME=/tmp/ava-adjust-review/data \
XDG_CONFIG_HOME=/tmp/ava-adjust-review/config \
XDG_CACHE_HOME=/tmp/ava-adjust-review/cache \
AVAMEDIA_CAPTURE_FFMPEG=/usr/bin/ffmpeg \
AVAMEDIA_CAPTURE_FFPROBE=/usr/bin/ffprobe \
  ~/.dotnet/dotnet run --no-build --project tools/AvaMedia.MultiStageCapture -- \
  adjust /tmp/ava-adjust-review/a-default-dark.png default

# 验证人员需要五张图时：
for state in default drag hover menu stale; do
  XDG_DATA_HOME=/tmp/ava-adjust-review/data \
  XDG_CONFIG_HOME=/tmp/ava-adjust-review/config \
  XDG_CACHE_HOME=/tmp/ava-adjust-review/cache \
  AVAMEDIA_CAPTURE_FFMPEG=/usr/bin/ffmpeg \
  AVAMEDIA_CAPTURE_FFPROBE=/usr/bin/ffprobe \
    ~/.dotnet/dotnet run --no-build --project tools/AvaMedia.MultiStageCapture -- \
    adjust "/tmp/ava-adjust-review/a-${state}-dark.png" "$state"
done
```

工具会进一步使用自己的临时 XDG 目录，并临时覆盖 `AVAMEDIA_STATE_DIRECTORY`；退出时还原环境变量并自动删除测试视频、任务结果、设置和缓存，输出 PNG 留在指定位置。预览、检查器起止帧和当前可见时间线缩略图加载完成后才截图，拖动状态还会等待真实吸附及对应预览帧；等待超过 45 秒会报出未就绪的阶段并失败。评审结束后删除 `/tmp/ava-adjust-review`。本模式只提供截图夹具，不运行功能回归、检测模型或像素对比。与 demo 的插画不同，预览采用 FFmpeg 合成测试画面；系统字体及平台控件尺寸以当前运行环境为准。
