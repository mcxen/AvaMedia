# 工具边界案例检查

日期：2026-10-09。范围为 `Catalog.All` 的 61 个入口，以及共享媒体探测、参数校验、进程、输出和队列路径。通过多轮网络检索阅读 FFmpeg、LosslessCut、HandBrake、Shotcut、Kdenlive、Olive、yt-dlp 的文档、源码和问题记录，再对照 AvaMedia 当前实现。外部 issue 是案例线索，不代表 AvaMedia 存在同一缺陷，也不代表上游当前版本仍未修复。

本轮完成源码检查和相关项目编译。按 `AGENTS.md`，未执行功能回归、媒体输出、截图、性能或压力测试，也未新增测试。下文“已有保护”指代码中有对应处理；不等同于本轮实测通过。

## 61 个工具的检查路径

| 工具 ID | 数量 | 检查实现及边界 |
| --- | ---: | --- |
| `mp4`, `delogo`, `voice-enhance`, `repair` | 4 | `MediaEngine`、`MediaFilters`、`VideoEncoding`：轨道选择、滤镜与 copy 冲突、码率、音轨缺失、字幕、取消及输出发布。普通重新编码的 HDR 处理仍有缺口，见后文。 |
| `video-compress` | 1 | `VideoCompression`、`VideoCompressionColor`：目标大小下限、尺寸、软件/硬件回退、HLG/PQ 色调映射、DV Profile 5 拒绝路径。 |
| `video-slim` | 1 | `VideoSlimming`：封面与真实多视频轨、SDR/HDR、隔行、音轨保留、源文件变化、样本过短、收益不足、私有临时输出。 |
| `join`, `mux` | 2 | `MediaEngine`、`SourceClipCopy`：独立输入编辑、静音补齐、轨道数量、重采样、合并时长、同源流复制、包时间边界。 |
| `split`, `extract-video` | 2 | `MediaEngine`：缺失视频/音频、真实视频映射、音频 copy 剪切、独立音轨与输出容器。 |
| `clip` | 1 | `QuickClipWindow`、`EditorWindow`、`ClipSplit`、`SourceVideoExport`：区间、零长片段、浮点尾段、逐帧定位、原属性输出、关键帧限制。 |
| `person-clip` | 1 | `PersonClipService`、`PersonClipSampling`、`SourceClipCopy`：视频/时长校验、分段采样、同源片段输出、取消。检测质量、片段边界与并行任务中的算法改动未实测。 |
| `frames` | 1 | `MediaEngine`：无画面拒绝、帧间隔有限值、选轨、目录输出；目录任务仍可能保留已完成帧。 |
| `player` | 1 | `Playback`、`PlayerWindow`、`Player/PlaybackVideoProfile`：默认轨道、封面菜单过滤、原始轨号、HDR 原生播放路径。 |
| `download` | 1 | `VideoDownloadService`、`YtDlpDownloadService`、`DirectVideoDownloadProvider`：URL、短文件名、保留名称、临时目录、断点续传、取消和最终文件移动。 |
| `auto-subtitle` | 1 | `SpeechSubtitleService`、`SubtitleOptions`：音轨、分块/空语音、字幕区间、外部路径转义、字幕模式与容器、源字幕类型。 |
| `video-summary` | 1 | `VideoSummaryService`、`VideoSummarySampling`：有限时长、选轨、短视频尾帧、采样和模型输出边界。实际推理与线上服务响应未验证。 |
| `info` | 1 | `MediaEngine.Probe`：真实视频、时长与帧率回退、旋转；超大 FFprobe 元数据 JSON 仍受进程日志上限影响。 |
| `audio-mp3`, `audio-flac`, `audio-wav`, `audio-m4a`, `audio-ogg`, `audio-aac`, `audio-ac3`, `audio-wma`, `audio-opus`, `audio-aiff` | 10 | `MediaEngine`、`VideoFormats`：音频映射、编码器、采样率/声道、Opus 48 kHz、无损编码参数、WAV RF64、文件发布。 |
| `audio-join`, `audio-mix` | 2 | `MediaEngine`：无音轨拒绝、输入独立编辑、混音归一化、不同输入格式重采样、时长计算。 |
| `audio-clip`, `audio-enhance` | 2 | `MediaEngine`、`MediaFilters`：copy 剪切、滤镜冲突、增强所需音轨与 FFmpeg 滤镜、参数有限值。 |
| `image-jpg`, `image-png`, `image-webp`, `image-bmp`, `image-tiff`, `image-gif`, `image-ico`, `image-avif` | 8 | `MediaEngine`、`HeifImage`：编码/质量、静态帧、GIF 调色板、ICO 尺寸、HEIF 主图、文件发布。动画转静态和通用图像转换的位深变化需要按用户期望判断。 |
| `image-compress` | 1 | `ImageCompression`、`AppleImageIO`：动画拒绝、透明通道、16 bit WebP 无损限制、WebP 尺寸上限、方向、主图和较小输出选择。 |
| `image-tools` | 1 | `MediaEngine`、`MediaFilters`、`CropGeometry`：缩放、正交旋转、尺寸与裁剪范围、图像编码。 |
| `image-ai`, `media-ai` | 2 | `MediaTagService`、采样与标签/重命名服务：有效图像/视频、真实视频采样、源文件与重命名冲突。模型精度和线上服务行为未验证。 |
| `images-pdf` | 1 | `MediaEngine`、`DocumentEngine`、`PdfTools`：HEIF 转换、页面选择、旋转、纸张和边距、无覆盖保存。 |
| `pdf-merge`, `pdf-split` | 2 | `PdfTools.Arrange`：输入与页码、重复选择/排列、旋转、空选择、目录输出、保存。 |
| `pdf-age`, `pdf-compress` | 2 | `PdfTools`：渲染参数、页码、输出预算、压缩增大时保留源文档、临时输出。加密和损坏 PDF 由库报告错误。 |
| `pdf-text`, `pdf-docx`, `pdf-xlsx` | 3 | `DocumentEngine`：文本提取、XML 非法控制字符、补充平面字符、Excel 单元格/行数限制、取消与文件发布。 |
| `text-pdf` | 1 | `DocumentEngine`：空文本、换行、代理对不截断、系统字体/TTC、页边距和新文件保存。 |
| `crop`, `rotate` | 2 | `BatchCrop`、`BatchRotate`、`SourceVideoExport`、`SourceVideoGpu`：跨视频尺寸、原始轨号、偶数裁剪、元数据旋转、源属性与硬件能力。 |
| `batch-rename` | 1 | `BatchRename`：无效/保留名称、重复目标、大小写、源目标循环、执行前复核、两阶段移动和回滚日志。 |
| `contact-sheet` | 1 | `BatchVideoTools`：像素总量上限、SAR/旋转、选择的视频轨时长、短片末帧、私有拼图和无覆盖发布。 |
| `zip`, `unzip` | 2 | `DocumentEngine`：同名输入、ZIP 路径越界、日期范围、大条目取消、私有条目写入、目标已存在。解压仍可保留之前已完成的条目。 |
| `dvd`, `iso` | 2 | `MediaEngine`：VOB 合并走统一输入校验；设备/文件读取和 ISO 取消后临时输出清理。未读真实光驱。 |

视频 18、音频 14、图片 12、文档 8、工具集 7、光驱设备 2，共 61。主界面路由与队列将上述入口接入各服务；没有运行逐个工具的 UI 验收。

## 本轮修复

1. 把 `attached_pic` / `timed_thumbnails` 与真实视频的识别集中到 `MediaStreams`。默认选第一个真实视频，保留原始 `v:N` 序号；纯音频封面不再使 `HasVideo` 为真。播放器菜单排除封面。
2. 转码、混流、视频流提取、原属性导出、GPU 导出、编辑预览和批量裁剪使用探测结果的实际轨号；码率回退的分母排除封面。瘦身复用共享探测，移除重复逻辑。
3. 容器缺少有效时长时，从真实音视频的 `duration`、`duration_ts × time_base` 或 `DURATION` 标签回退。帧率从无效的 `avg_frame_rate` 回退到 `r_frame_rate`；这只是标称帧率，不将 VFR 推断为 CFR。
4. 90°/270° 元数据判断容许小数误差；避免 `89.999…` 导致预览、图片方向或拼图尺寸错误。
5. 逐帧时间戳改为逐行解析 CSV，避免大 GOP/高帧率 JSON 被日志上限截断。起始时间与时间戳拒绝 NaN/Infinity；定位和编码参数增加静态范围检查。
6. 未指定帧率的单输入视频重新编码显式使用 VFR；现代软件编码器使用输入时间基。合并仍按现有统一帧率设计执行。旧编码器和硬件的实际时间戳行为未实测。
7. 单输入、纯音频、流复制剪切改为输出端 seek，并仅对此路径加 `-copyinkf`，处理稀疏音频关键包标记导致的开头跳过。视频流复制不使用此选项。
8. WAV 输出统一指定 `-rf64 auto`，跨过 RIFF 32 bit 大小边界时允许 RF64。
9. 软件单文件转码与硬件输出完成后检查非空和取消状态，再移动至正式输出；失败或取消清理私有文件。同源合并、ISO 复制也延后发布。逐帧目录输出仍保留已完成帧。
10. 进程输出读取或进度回调异常时终止子进程，防止停止排空管道后等待退出卡住；取消回调处理进程退出竞态中的 Win32 异常。
11. ZIP 压缩和解压按固定缓冲区复制并检查取消；ZIP、单个解压条目、TXT/DOCX/XLSX 完成后再发布。源文件日期超出 ZIP 范围时使用默认有效日期。
12. DOCX/XLSX 删除 XML 1.0 禁止的控制字符，同时保留有效补充平面 Unicode 字符。XLSX 超过 32767 UTF-16 单元的长行分成多行，不切断代理对；超过 1048576 行时给出 TXT 导出提示，避免生成越界工作表。

## 外部案例与对应结论

| 来源 | 极端案例 | 本地检查结论 |
| --- | --- | --- |
| [FFmpeg stream specifiers](https://ffmpeg.org/ffmpeg.html#Stream-specifiers)；[Shotcut 代理源码](https://github.com/mltframework/shotcut/blob/master/src/proxymanager.cpp) | 封面/缩略图也属于 video stream | 修复共享识别；用真实视频默认值和原始轨号。 |
| [LosslessCut 排障文档](https://github.com/mifi/lossless-cut/blob/master/docs/troubleshooting.md) | 流复制只能在关键帧附近起始，可能出现黑帧、音画起点差异 | 区间校验已有；任意帧精准 copy 不属于当前能力，需重新编码。 |
| [LosslessCut #1216](https://github.com/mifi/lossless-cut/issues/1216)、[#330](https://github.com/mifi/lossless-cut/issues/330) | seek 到相邻关键帧，帧编号与请求时间并不相等 | 编辑器定位按实际帧时间戳；导出 copy 仍受编码边界限制。 |
| [LosslessCut #317](https://github.com/mifi/lossless-cut/issues/317) | 裁切后的音画不同步 | 同源合并已有包 PTS/时长边界处理；不同输入和异常时间戳未实测。 |
| [LosslessCut #2494](https://github.com/mifi/lossless-cut/issues/2494) | 非零 start_time、小于 GOP 的片段缺视频，合并映射失败 | 同源路径会读取视频包并拒绝没有有效视频边界的片段；未增加 Smart Cut。 |
| [LosslessCut 音频 copy 源码](https://github.com/mifi/lossless-cut/blob/master/src/renderer/src/util/streams.ts) | M4A 音频包关键标记稀疏，剪切跳过开头 | 对纯音频 copy 使用输出 seek 和 `copyinkf`；本轮修复。 |
| [LosslessCut 旋转讨论](https://github.com/mifi/lossless-cut/discussions/2373) | FFmpeg 升级后旧 rotate metadata 设置方式行为改变 | `SourceVideoExport` 已使用输入 `display_rotation`；新增角度容差。 |
| [FFmpeg 编码选项](https://ffmpeg.org/ffmpeg.html) | VFR、输入/编码时间基差异导致量化、丢帧或重复帧 | 普通单输入未设置 FPS 时显式 VFR；现代软件编码采用 demux 时间基。 |
| [Shotcut #1892](https://github.com/mltframework/shotcut/issues/1892) | 项目帧率与导入源帧率不一致 | 合并按第一输入/显式 FPS 统一；普通单输入路径不主动强制 CFR。 |
| [FFmpeg concat 文档](https://ffmpeg.org/ffmpeg-formats.html#concat) | 容器时长不准、流时间基不一致导致后续片段重叠/空隙 | 原格式合并仅允许同源、相同轨道；使用包边界且关闭 H.264 自动转换。 |
| [Kdenlive 音频 FAQ](https://docs.kdenlive.org/en/troubleshooting/faq.html) | 混合音源、VBR 同步、剪切接缝噪声 | Join/Mix 已重采样和重置时间戳；接缝噪声与听感需要实际音频验收。 |
| [FFmpeg WAV/RF64 文档](https://ffmpeg.org/ffmpeg-formats.html#wav) | RIFF WAV 超过约 4 GiB | 统一启用 RF64 自动切换；旧播放器兼容性未实测。 |
| [HandBrake #1100](https://github.com/HandBrake/HandBrake/issues/1100) | 外部 SRT 名称含逗号等特殊字符 | AvaMedia 使用参数数组；字幕滤镜值已有双层转义，不套用上游 CLI 的逗号列表。 |
| [HandBrake #3746](https://github.com/HandBrake/HandBrake/issues/3746) | 非方形像素/变形视频尺寸处理 | 拼图读取 SAR，合并设置方形像素；通用预览、编辑坐标与所有 SAR 组合仍需实测。 |
| [HandBrake #5976](https://github.com/HandBrake/HandBrake/issues/5976) | Dolby Vision Profile 7 与 AV1 转码的特定版本回归 | 压缩具基础层处理/拒绝策略；动态元数据和普通转码仍有缺口，不声称完整 DV 支持。 |
| [Shotcut #1824](https://github.com/mltframework/shotcut/issues/1824) | 10 bit 线性色彩处理异常 | 原属性编码保留像素格式，压缩显式色调映射；普通转换默认 8 bit 的边界仍保留。 |
| [Olive #2397](https://github.com/olive-editor/olive/issues/2397) | 五小时 MKV 导出内存增长并崩溃 | 转码经外部 FFmpeg，进程日志有上限；字幕/PDF/模型结果仍存在随输入增长的内存集合，未做压力测试。 |
| [yt-dlp #1136](https://github.com/yt-dlp/yt-dlp/issues/1136) | 标题、扩展名及临时前缀使文件名超过文件系统限制 | 下载标题已有 70 个文本元素限制及短临时目录；全路径长度仍随目标目录变化。 |
| [yt-dlp #9547](https://github.com/yt-dlp/yt-dlp/issues/9547) | 外部字幕格式转换失败 | 下载错误保留日志；本地字幕映射校验文本/图形字幕及容器，未访问真实站点验收。 |
| [Excel 官方限制](https://support.microsoft.com/en-us/excel/excel-specifications-and-limits) | 单元格 32767 字符，单表 1048576 行 | 长行切分并检查行数；本轮修复。 |
| [XML 字符规则](https://learn.microsoft.com/en-us/dotnet/api/system.xml.xmlconvert.isxmlchar?view=net-10.0) | PDF 提取含 NUL/控制字符，DOCX/XLSX XML 无效 | 写入前清理非法字符；本轮修复。 |

## 已有保护与仍需处理的边界

已有保护还包括：裁剪范围采用溢出安全计算；copy 与画面/音频滤镜互斥；字幕变速/容器不兼容校验；短样本和无有效帧处理；HEIF 主图选择；动画压缩拒绝；WebP 尺寸/位深限制；PDF 页码与纸张范围；ZIP 字面路径越界；批量重命名冲突复核及回滚；下载暂存、请求修订与最终发布。上述路径均按源码判断。

| 状态 | 边界 | 后续范围 |
| --- | --- | --- |
| 确认的能力缺口 | 普通 `MediaEngine.BuildArguments` 重新编码默认输出 8 bit，仅 `VideoCompress` 接入 HDR 色调映射；普通 HDR 转换、HDR/SDR 混合合并可能产生错误观感 | 需设计统一颜色目标及逐输入处理，再用 PQ/HLG/DV 样本验证。当前 HDR→SDR 应走视频压缩；保留源属性需使用相应输出方式。 |
| 确认的能力边界 | Fast Copy 受关键帧/音频包边界限制；没有 Smart Cut | 精准边界应选择重新编码；不把 UI 帧定位精度等同于流复制导出精度。 |
| 确认的资源边界 | 普通 FFprobe 元数据 JSON 有日志上限；PDF/DOCX/XLSX 构造仍累积文本，超大目录/归档没有统一预算 | 流式元数据/文档写入和可取消资源预算是后续工作。本轮只修复逐帧 JSON 截断及条目取消。 |
| 输入相关，未验证 | 非单调 DTS、负 PTS、错误容器时长、丢包、损坏索引、隔行、非正交/镜像显示矩阵、极短 VFR | 需要对应真实样本和用户授权的媒体输出检查；不按扩展名猜测修复。 |
| 环境相关，未验证 | 磁盘满、断开的移动盘/网络盘、权限、链接目录、超长 Unicode 路径、源文件并发修改、真实光驱 | 单文件无覆盖暂存减少半成品暴露；不保证跨文件系统/掉电事务。ZIP 字面越界校验不能代替对已有符号链接的路径约束。 |
| 平台相关，未验证 | GPU 驱动、10/12 bit、4:2:2/4:4:4、硬件 VFR/RF64 播放兼容性 | 编码候选、失败回退和源属性能力检查已有；仍需平台实测。 |

本报告覆盖全部现有工具的入口及共享实现；不作“所有工具无 bug”或“全部极端视频可处理”的结论。
