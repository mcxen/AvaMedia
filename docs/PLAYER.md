# 播放器

“格式播放器”打开独立播放窗口。启动参数 `AvaMedia.Desktop.exe --play "视频.mp4"` 或直接传入文件路径，也直接进入播放器；多文件按输入顺序连续播放。打开多个文件或拖入文件可替换播放列表。

布局参考 [PotPlayer 官方界面](https://potplayer.tv/)：顶部紧凑标题栏，中间等比画面，底部时间轴与单行控制栏。播放、停止、上一项、下一项和打开位于左侧，随后是当前位置 / 总时长；右侧为音量、速度、设置、播放列表和全屏。右键打开功能菜单，播放列表在画面右侧展开。全屏闲置两秒隐藏控制栏，移动指针恢复。

Light、Dark、Mac OS 9 使用相同布局和组件指标；Mac OS 9 使用现有 Platinum 标题栏。菜单可选择真实音视频轨、播放速度和画面比例，也可查看媒体信息。紧凑窗口收起音量滑块，音量快捷键和静音仍可用。没有复用 PotPlayer 的图标、皮肤文件或代码。

| 快捷键 | 操作 |
| --- | --- |
| Space | 播放 / 暂停 |
| Enter / Alt+Enter；Esc | 切换全屏；退出全屏 |
| ← / → | 后退 / 前进 5 秒 |
| Shift+← / →；Ctrl+← / → | 跳转 30 秒；60 秒 |
| ↑ / ↓；M | 音量加减 5%；静音 |
| X / C；Z | 减速 / 加速 0.1×；正常速度与上次速度切换 |
| D / F | 暂停并定位前 / 后一个真实源帧 |
| Backspace；F4 | 回到起点；停止并回到起点 |
| Page Up / Page Down | 上一 / 下一文件 |
| F3 / Ctrl+O（Mac 可用 Cmd+O） | 打开媒体文件 |
| F5；F6；F1 | 播放设置菜单；播放列表；快捷键说明 |

以上是本客户端支持的常用 PotPlayer 式键位，其他键位没有接管。文本输入保留编辑快捷键。双击画面切换全屏，画面上滚轮调节音量。

## 启动与播放

`IPlaybackSession` 分离播放窗口与解码实现；播放器和剪辑器共用 `Playback`。视频直接经 FFmpeg 管道输出 BGRA，音频直接输出 48 kHz 双声道 PCM；音频缓冲上限半秒，不创建整条音轨的 WAV 缓存。两条管道同时启动，首帧发布后启动媒体时钟；音频启动异常会显示原因并保留视频播放。

暂停保留解码进程、当前位置和音频缓冲，恢复播放不重新启动 FFmpeg。定位和换轨替换旧会话；连续拖动只交付最后请求，暂停可取消尚在进行的定位。音画使用同一播放时钟；倍速通过视频调度与 FFmpeg `atempo` 同步改变，保留声音音高。视频先结束时保留最后画面，到媒体或所选区间结束后停止。

Windows 发布启用 ReadyToRun，减少应用冷启动的即时编译；普通开发构建仍可直接运行。本地测量入口：

```powershell
./scripts/Measure-PlayerStartup.ps1 -Application artifacts/release/1.1.0/win-x64/AvaMedia.Desktop.exe -Media '视频.mp4' -Runs 5 -Skin Dark
```

每次启动独立应用进程，记录从进程创建到窗口打开、到首帧发布的延迟，输出各次结果、中位数、P95 和实际窗口截图。保留操作系统文件缓存，不把这组测量描述为重启系统或清空磁盘缓存后的结果。媒体编码、磁盘、CPU 与音频设备会影响数值。

`tests/AvaMedia.PlayerTests` 检查真实解码 PCM、首帧源时间、缓冲上限、暂停复用、倍速边界、长音频启动、定位取消、连续播放、键盘操作和三套皮肤的默认 / 最小布局；音频专项使用可读取实际 PCM 的测试输出设备。Windows 原生窗口与实际 WaveOut 通过启动测量另外检查。已加入 `scripts/Verify.ps1`。

实现参考：[FFmpeg 命令文档](https://ffmpeg.org/ffmpeg.html)、[音频速度滤镜](https://ffmpeg.org/ffmpeg-filters.html#atempo)、[NAudio 2.2.1 缓冲实现](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Core/Wave/WaveProviders/BufferedWaveProvider.cs)。客户端维持 AGPL-3.0-only，外部 FFmpeg 和 NAudio 沿用现有许可处理。
