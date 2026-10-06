# 播放器与大文件性能记录

2026-10-06 在同一台 Windows x64 机器上比较 `v1.1.6` 与本次开发代码。两组均为 .NET 8 Release 开发构建，浅色主题，每个场景独立进程运行三次，取中位数；保留操作系统文件缓存。这里的启动数据不是重启系统或清空缓存后的磁盘冷读。原始采样的简化归档见 [测量数据](measurements/player-20261006.json)。

2026-10-07 后续调整播放对象的私有帧缓冲、图标与队列预览的持有范围，并停止空闲队列计时；见 [空闲内存](IDLE-MEMORY.md)。下列数据保留为 10 月 6 日构建的历史结果，不代表本次内存修改已经重新测量。

## 输入与范围

| 场景 | 实际文件 | 内容 |
| --- | --- | --- |
| 4K 大文件 | 5,226,847,190 bytes | 3840×2160、30 fps、14 秒，索引完整的 rawvideo AVI + PCM；真实媒体包超过 4 GiB |
| 1080p60 | 69,056,951 bytes | 1920×1080、60 fps、14 秒，MPEG-4 + AAC |
| 长音频 | 1,382,400,078 bytes | 两小时、48 kHz 双声道 PCM WAV |
| 大章节元数据 | 2000 章 | 从 1080p60 文件无损重封装，FFprobe JSON 为 606,022 字符 |
| 队列 | 5000 项 | 完整参数，队列 JSON 为 11,322,783 bytes |

素材由 FFmpeg 生成，不使用稀疏填充或改名假文件。4K 以现有播放器的 1280×720 上限呈现；这是大输入、内存和 I/O 检查，不代表 4K 原分辨率 GPU 渲染或高复杂度 HEVC 的性能。

CPU / 分配 / 工作集来自 AvaMedia 父进程，不包含 FFmpeg 子进程及 GPU 显存。CPU 与分配覆盖开窗、四秒播放、暂停、恢复、定位和关闭整个固定操作序列；不是 CPU 使用率。Headless Skia 测试使用真实 FFmpeg 和实际 PCM 消费器，显示帧数指交付到窗口的帧，不代表显示器扫描输出。UI P95 指后台优先级回调从投递到执行的排队时间。

## 前后数据

| 场景 | 父进程 CPU，ms | 托管分配，MiB | 峰值工作集，MiB | 交付 fps |
| --- | --- | --- | --- | --- |
| 4K 大文件 | 2171.88 → 1765.63（−18.7%） | 19.74 → 18.31 | 94.98 → 87.81 | 29.96 → 29.95 |
| 1080p60 | 2921.88 → 2765.63（−5.3%） | 24.81 → 22.16 | 94.90 → 88.35 | 59.87 → 59.93 |
| 两小时音频 | 1515.63 → 1062.50（−29.9%） | 9.35 → 6.64 | 84.42 → 77.64 | 音频 |

| 场景 | 打开到首帧 / 首次时钟反馈，ms | 定位，ms | 恢复后画面 / 时钟反馈，ms | UI 排队 P95，ms |
| --- | --- | --- | --- | --- |
| 4K 大文件 | 295.74 → 283.21 | 159.19 → 159.04 | 48.02 → 64.02 | 15.99 → 16.00 |
| 1080p60 | 283.18 → 260.03 | 159.96 → 146.82 | 48.02 → 50.84 | 11.98 → 13.33 |
| 两小时音频 | 311.72 → 287.83 | 191.20 → 188.84 | 64.02 → 143.04 | 16.00 → 16.00 |

降低音频 UI 时钟通知频率到 10 Hz 后，测试观察到的恢复时钟反馈变慢；音频输出仍在恢复操作中立即调用 `Play`，该数值不是声音开始输出的延迟。没有证据表明 UI 排队延迟改善，不能把 CPU 降低解读为所有操作更快。

另用相同 4K 源的无音轨 AVI 测量真正的 Windows 桌面窗口：从创建应用进程到首帧，中位数 **693.43 → 594.94 ms（−14.2%）**。两组均各三次，P95 为 694.96 → 643.48 ms。此构建没有发布包的 ReadyToRun；不能把这组数据直接当作安装包表现。测试时机器的 WaveOut 设备不可用，因此原生启动比较使用无音轨输入，音频管道通过 PCM 测试设备验证，未完成扬声器听音验收。

## 细节与大批次检查

播放器专项通过 147 项检查，包含真实 PCM、暂停复用、速度、定位、取消、目录播放列表和三套皮肤的布局；新增隐藏画面后 PCM 继续消费、恢复不重启解码器两项。默认不生成截图。

细节专项通过 18 项检查：经典样式延迟加载及缓存；大于 4 GiB 的源与输出大小；一万次进度变化不重新读取大小；旧读取与关闭取消；5000 项后台保存；旧保存不能覆盖清空和新同步保存；完整章节 JSON；大文件 SHA256 与框架独立计算一致、带中间进度并可取消；隔离注册表中的全部扩展名、带引号激活命令、保留现有默认应用与卸载只清理自身项。

最后一次 5000 项保存的 UI 调用入队约 0.22 ms，后续序列化和写盘在后台；这不是完整写盘耗时。安装器经本地 Inno Setup 编译检查；本次未实际安装到用户系统，也未把测试注册表项写入真实文件关联。

## 复现

需工程的 .NET SDK 与 LGPL FFmpeg 工具，生成素材约占 12 GB 磁盘（包含用于原生启动的同源无音轨副本）。以下命令显式执行性能 / 大文件专项，日常编译不自动运行它们。

```powershell
./scripts/Prepare-PlayerLargeFiles.ps1
./scripts/Measure-PlayerPerformance.ps1 -Runs 3 -Skin Light -NativeStartup
& ./.tools/dotnet/dotnet.exe tests/AvaMedia.PlayerTests/bin/Release/net8.0/AvaMedia.PlayerTests.dll --details
# 基础播放器专项；截图仅在明确传入 --capture 时生成
& ./.tools/dotnet/dotnet.exe tests/AvaMedia.PlayerTests/bin/Release/net8.0/AvaMedia.PlayerTests.dll
```

素材脚本拒绝覆盖已有文件；需要再次生成时指定新的 `-Output`，测量对应指定 `-Fixtures`。每次测量的 JSON 记录源文件实际大小、主题、帧、进程数量、PCM 缓冲、失败信息及代码版本；总报告记录是否存在未提交修改。原生启动测试需要可用桌面，带音轨输入还需要实际音频输出设备。

## 开源实现评估

| 实现 | 参考能力与本次决定 |
| --- | --- |
| [mpv / libmpv render API](https://github.com/mpv-player/mpv/blob/master/include/mpv/render.h) | 提供独立渲染接口及线程约束；可作为未来原生 GPU 呈现后端。本次保留 `IPlaybackSession` + FFmpeg，优化已有复制路径，没有引入或复制 libmpv |
| [NAudio 2.2.1 BufferedWaveProvider](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Core/Wave/WaveProviders/BufferedWaveProvider.cs) | 已使用的 PCM 缓冲实现；继续按半秒上限和反压消费，不缓存整条音轨。现有 MIT 许可通知保持在第三方清单 |
| [CommunityToolkit AsyncRelayCommand](https://github.com/CommunityToolkit/dotnet/blob/main/src/CommunityToolkit.Mvvm/Input/AsyncRelayCommand.cs) | MIT 异步命令实现可统一任务状态与取消；当前窗口已有生命周期取消和接口工厂，本次未增加 MVVM 包或重写命令层 |
| [.NET ArrayPool](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1) / [FileStreamOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestreamoptions) | 用运行时提供的缓冲复用、异步与顺序读取完成帧传输和 SHA256 工具，避免新增依赖；摘要算法继续使用框架 `IncrementalHash` |

上表中的未集成项目是评估来源，没有作为客户端依赖发布。原创实现继续 AGPL-3.0-only，本次未增加第三方代码或改变现有依赖许可处理。
