# FFmpeg 交互响应与厨房人物片段调查

2026-10-13。三个子 agent 分别调查 FFmpeg 交互调用链、开源检测实践、人物任务生产链；主 agent 在真实厨房视频上执行检测、队列导出和反复对照。只验证本次人物任务，不运行全量回归或界面截图验收。

**FFmpeg 封面和拖拽响应**

首要瓶颈是 EditorWindow.Seek 的 120 ms 尾沿等待：每次拖动取消前一个请求并重置等待，连续拖动难以更新画面。停手后仍需新建 FFmpeg 进程、编码 PNG、读取并解码。主预览和起止小图均取 960×540；小图另外等待 180 ms 且串行抽两端。MediaPreviewCache 的两个全局槽没有交互优先级；FrameStripCache 生成使用 CancellationToken.None，窗口关闭只取消等待，不终止无人需要的生成。

| 优先级 | 建议 | 对应位置 |
| --- | --- | --- |
| 1 | 拖动期间按约 50 ms 合并最新目标，持续出近似帧；松手立即精确寻帧 | EditorWindow.axaml.cs / Seek |
| 2 | 起止小图降到实际控件所需的 240–320 px，只刷新变化侧；主预览按显示尺寸 | EditorWindow.axaml.cs / ReadFrame |
| 3 | 在现有进程预算内让交互请求优先；无人订阅的胶片生成可取消 | MediaPreviewCache.cs / FrameStripCache.cs |
| 4 | 图片先进入内存并返回，磁盘持久化及缓存清理移出交互返回路径 | MediaPreviewCache.cs / PreviewCacheStore.cs |
| 5 | 后续接入每个视频一个持续解码会话，直接像素输出 | Playback.cs / MediaEngine.Thumbnail |

[thumbfast](https://github.com/po5/thumbfast/blob/master/thumbfast.lua) 使用长驻暂停的 mpv、raw BGRA、50 ms 合并寻帧，先关键帧定位、静止后精确定位。[LosslessCut](https://github.com/mifi/lossless-cut/blob/master/src/renderer/src/ffmpeg.ts) 使用小尺寸 JPEG、逐帧回调与取消信号。[FFmpeg 官方](https://ffmpeg.org/ffmpeg.html#Advanced-Video-options)说明硬件帧取回 CPU 也有开销，硬解不保证单张封面更快。现有输入前 -ss、硬解及软件回退、缓存和取消已存在。本轮这些交互建议为调研结果，未修改播放器/封面调用链，未测提速倍数。

**人物检测与开源实践**

默认为单 NanoDet、2 fps、阈值 0.35。厨房原始运行有 378 次逐帧推断，复用、黑灯、无画面、免检测均为 0；这些机制不能解释漏检。COCO 人体与 MediaPipe 人体检测不能稳定覆盖只露手指、前臂的料理特写。

不同 artifact 不能共享一种输入。YOLOX 为 RGB 0–255、左上角灰 114 letterbox；MediaPipe person 为 RGB [-1,1]、中心零填充；Palm 为 RGB [0,1]、黑填充、NHWC [1,192,192,3]。NanoDet 原作者训练采用 BGR 均值/方差，而 OpenCV demo 采用 RGB，二者不一致；本轮对同一厨房实帧比较两种输入，保留 BGR 训练归一化，不把 OpenCV demo 的 RGB 直接当作正确性结论。NanoDet 同输入 CPU/Core ML 的 20 次对照最大分数差小于 0.000001，未发现该模型的加速分数漂移。

资料：[YOLOX adapter](https://github.com/opencv/opencv_zoo/blob/main/models/object_detection_yolox/demo.py)、[NanoDet 训练配置](https://github.com/RangiLyu/nanodet/blob/main/config/nanodet-plus-m-1.5x_416.yml)、[NanoDet OpenCV demo](https://github.com/opencv/opencv_zoo/blob/main/models/object_detection_nanodet/demo.py)、[MediaPipe person](https://github.com/opencv/opencv_zoo/blob/main/models/person_detection_mediapipe/mp_persondet.py)、[Palm adapter](https://github.com/opencv/opencv_zoo/blob/main/models/palm_detection_mediapipe/mp_palmdet.py)。模型下载固定 revision d4938dfc9d4ec5d098bfa33e98b3f3345a236586 与 SHA256。

三人体模型组合在该厨房视频首帧出现 MediaPipe 非有限分数，运行并未抛出 ONNX 异常，原先自动回退因而未触发。已增加分数校验后 CPU 重试；同素材组合完整运行成功，日志明确记录 MediaPipe 转 CPU。其余检测器保留各自后端。

新增可选「保留手部特写」：人体判定不保留时，再在同一张解码画面上运行约 3.9 MB 的 Palm；手部证据与人体结果为 OR，不占用人体多数投票。多局部区域补检经过同素材对照后未采用：它明显增加误保留与推断次数。整帧补检也有准确率取舍，因此默认关闭，界面提示可能保留部分无人物画面，参数变更要求重新检测。

后续要继续提高稳定性，应保留检测框并验证空间位置和跨帧关联，用类似 [ByteTrack](https://github.com/FoundationVision/ByteTrack) 的低分框关联延续已确认的轨迹；不能简单把所有低分都当有人，或把全部低分都当无人。该方案本轮未实现。仍需包含手指遮挡、器具遮挡及无人锅菜的独立标注集，才能校准阈值和评价通用准确率。

**厨房素材对照**

源文件：/Users/mcx/Movies/AvaMedia/【我做了20年的拿手菜，西红柿土豆炖牛腩】.mp4。188.766 秒、480×852、AV1 / AAC。源 SHA256：A7F43815EA79492182F486FA35826316F9CE2D0FD22B5C2F36E73D1DAB0733C6。

人工核验点包括半身 48、121、137、181 秒，手部 12、109、115.5、151.5 秒，以及 14 个只有锅菜的对照点。参考图和原始结果在 artifacts/person-kitchen-20261013/。

| 样本结果 | 原默认 NanoDet | NanoDet + 整帧手部补检 |
| --- | ---: | ---: |
| 4 个半身点保留 | 4/4 | 4/4 |
| 4 个手部点保留 | 1/4 | 3/4 |
| 14 个无人物点排除 | 13/14 | 8/14 |

这是抽样区间覆盖结果，包含前后余量与时间合并的影响，不是逐帧模型精确率，也不能外推为整段或其他素材准确率。115.5 秒倒水、151.5 秒加料/切菜手部被找回；109 秒仅露手指持勺的画面仍落在保留边界外。31.5、33、104、117、155.5 秒等对照点有误保留，手部选项增加了召回，也增加了误保留。人物模型稳定识别问题仍未完全解决。

**生产队列与导出**

使用实际 QueueService → MediaEngine → PersonClipService 分析，再用桌面同一 QuickClipWorkflow.PrepareJoinedJobs → QueueService 导出。每次检查输出流、整段 -xerror 解码、源文件长度/mtime/SHA256，并记录冷运行与缓存运行的区间与哈希。

发现 Fast Copy 的独立问题：22 个请求应保留 150.8285 秒，却生成 218.368 秒。逐段输入前 -ss 配合流复制带回关键帧前卷，拼接计入了重复源 packet；6551 个输出视频 packet 超过源 5663 帧。输出能解码不代表内容时间正确。[FFmpeg -ss 官方语义](https://ffmpeg.org/ffmpeg.html#Main-options)支持该解释。

人物任务默认导出已改为 MP4 精确重编码。Fast Copy 仍可手动选择，其边界与重复前卷限制写入功能文档；本轮未修改通用 SourceClipCopy，避免影响用户主动重排/重复的普通快速剪辑段。首个精确导出为 150.8328 秒，与 150.8285 秒请求相差 0.0043 秒；完整解码成功、源文件不变。

最终 BGR + 可选整帧手部补检版本的队列复算和交付数据见本文件末尾，手部选项在该验收运行中显式开启。相关 Core、Desktop 编译通过；没有运行全量测试、截图或其他媒体任务。

| 最终生产运行 | 检测秒数 | 导出秒数 | 区间数 | 预期保留秒数 | 输出秒数 | 整段解码 / 源文件 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 冷复算（手部开启） | 33.833 | 82.259 | 18 | 157.266000 | 157.266111 | 通过 / 未变 |
| 缓存重跑（手部开启） | 0.259 | 66.647 | 18 | 157.266000 | 157.266111 | 通过 / 未变 |

两次区间逐一一致；缓存重跑无需新模型推断。输出 SHA256 相同：true。最终输出为 H.264 / AAC，复核 48 秒半身、115.5 秒倒水与 151.5 秒加料的源时刻对应输出帧，画面一致。耗时受共享机器上其他工作影响，仅记录该素材的实测，不承诺通用提速。

交付文件：/Users/mcx/Movies/AvaMedia/【我做了20年的拿手菜，西红柿土豆炖牛腩】 [kitchen-verified].mp4。冷运行输出 SHA256：A08FB0F5A1990113F0E3887EA6A5AA3879DB561D39D4515D62757BD09B1D6978。原始证据：artifacts/person-kitchen-20261013/final-summary.json、manual-labels.json、kitchen-verified.json、kitchen-repeat.json、output-frame-mapping.json。

可复现入口是本轮本地 Kitchen.csproj：` .tools/dotnet/dotnet run --project artifacts/person-kitchen-20261013/Kitchen.csproj -- queue kitchen-repeat precise hands `。`cold` 仅清理这一源文件/参数/代码版本的结果缓存，`delivery` 使用设置中的输出目录；所有输出均使用新名称，不覆盖源视频。验收 harness 与媒体只存于忽略的 artifacts，不新增全量测试或回归入口。
