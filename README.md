# 天池万象转换 · AvaMedia

**简体中文** · [English](README.en.md)

[![CI](https://github.com/mcxen/AvaMedia/actions/workflows/ci.yml/badge.svg)](https://github.com/mcxen/AvaMedia/actions/workflows/ci.yml)
[![Release](https://github.com/mcxen/AvaMedia/actions/workflows/release.yml/badge.svg)](https://github.com/mcxen/AvaMedia/actions/workflows/release.yml)
[![License: AGPL-3.0-only](https://img.shields.io/badge/license-AGPL--3.0--only-blue)](LICENSE)

面向 **Windows 和 macOS** 的开源桌面媒体工具，同时提供 **Linux x64 开发包**。媒体转换、压缩、导出、下载和 AI 分析共用批量任务队列；播放、看图与参数编辑使用独立窗口。

使用 **C#、.NET 8 和 Avalonia 11** 开发，提供简体中文 / English 界面与 Light、Dark、Mac OS 9 · Platinum 与 Windows XP · Luna 四套皮肤。当前仍为开发版，功能范围和已验证结果见 [功能说明](docs/FEATURES.md)。

[下载](https://github.com/mcxen/AvaMedia/releases/latest) · [MCP 与网页远程管理](#mcp-与网页远程管理) · [界面截图](#界面截图) · [从源码运行](#从源码运行) · [报告问题](https://github.com/mcxen/AvaMedia/issues)

## 下载与安装

从 [GitHub Releases](https://github.com/mcxen/AvaMedia/releases/latest) 下载对应平台的安装包。

| 平台 | 文件 | 安装方式 |
| --- | --- | --- |
| Windows x64 | `AvaMedia-<版本>-win-x64-setup.exe` | 运行安装器，包含快捷方式与卸载入口 |
| Windows x64 便携版 | `AvaMedia-<版本>-win-x64-portable.zip` | 解压后运行 `AvaMedia.Desktop.exe` |
| macOS Apple Silicon | `AvaMedia-<版本>-osx-arm64.dmg` | 打开后将 `天池万象转换.app` 拖入 Applications |
| Linux x64 开发包 | `AvaMedia-<版本>-linux-x64.tar.gz` | 解压后运行 `./avamedia`，需要可用的图形桌面与系统依赖 |

macOS 安装包面向 **Apple Silicon（ARM64），声明最低 macOS 13.4**；该最低版本的完整设备验收状态见 [平台说明](docs/PLATFORMS.md)。应用采用 ad-hoc 签名，尚未进行 Developer ID 签名和公证，首次启动可能需要在系统“隐私与安全性”中允许打开。

**Windows / macOS** 包内置 FFmpeg / FFprobe、yt-dlp 和 QuickJS-NG，应用所需的 .NET 8 与 ASP.NET Core 运行时由原生启动器检查。运行时不完整时，点击“安装运行时”下载、校验并安装到当前用户目录；已有完整运行时会跳过安装步骤。首次启动还需选择皮肤和使用偏好，完成后进入软件，后续沿用保存的运行时位置和设置。无需管理员权限、开发 SDK 或另装 Python，详见 [首次使用](docs/FIRST-RUN-SETUP.md)。

**Linux** 包自带 .NET 运行时，需要自行准备 FFmpeg / FFprobe 9.x、yt-dlp，以及要求 JavaScript 运行时的下载所需的 QuickJS-NG。当前内嵌声音预览和浏览器嗅探未接入 Linux，完整桌面功能及真实发行版安装尚未验收，见 [Linux 说明](docs/LINUX.md)。安装包 SHA256 与 Windows / macOS 内置媒体工具的源码归档链接位于对应 Release 说明中。

## 主要功能

| 功能 | 支持内容 |
| --- | --- |
| 视频转换与压缩 | MP4、MKV、WebM、AVI、MOV、TS 等；画质、码率或目标体积；Apple、NVIDIA、Intel、AMD 硬件转码与软件回退；iPhone HDR 转 SDR |
| 视频瘦身 | 按真实画面采样、试编码和质量评分选择参数，预估体积后导出；支持逐行 SDR 视频，保留源文件 |
| 剪辑与批量处理 | 多文件、多片段、按段数 / 时长 / 时间点分割；裁剪、旋转、镜像、速度、淡入淡出；批量裁剪、方向建议、重命名与多宫格截图 |
| 音轨与字幕 | 合并、混流、音视频流提取；选轨、多音轨保留、采样率、声道、音量与音频效果；字幕烧录或独立字幕轨 |
| 自动字幕与人声增强 | 离线语音识别、字幕文字与时间校对、样式和位置调整；导出 SRT / ASS 或烧录 MP4 / MKV；音视频人声降噪 |
| 图片转换与压缩 | HEIC / HEIF 输入；JPEG、WebP、PNG 压缩；实际编码后的体积与前后对比；只保存变小的压缩结果并保留原图；另有图片格式转换、缩放与旋转 |
| 天池看图 | 目录和压缩包图片浏览、双页、动画、EXIF、幻灯片、收藏及批量导出 |
| PDF 工作区 | 页面预览、选页、排序、旋转、拆分与合并；图片 / TXT 排版为 PDF；PDF 做旧、压缩及文本提取 |
| 自动标签分类 | 图片 / 视频标签、中文词库、NSFW 复核、画面描述、分数与采样曲线，以及 TXT / JSON 导出 |
| 自动媒体整理 | 按标签、语义、时长、姿势、人物、相似服装等分组；人工调整、目录预览、复制 / 移动与撤销 |
| 视频总结 | 本地或线上 AI，结合字幕 / 语音和可选画面分析，生成摘要、章节、内容分析与报告 |
| 保留有人片段 · Beta | 多模型人物检测、免检测区间、结果查看及原格式 / 重新编码导出 |
| 视频下载 | 分享文本、批量链接、播放列表 / 分 P / 相册；画质、视频 / 音频、字幕、登录态、代理与失败重试；Windows / macOS 内嵌浏览器嗅探 |
| 天池播放器 | 独立播放窗口、目录播放列表、全屏、倍速、选轨、逐帧定位、当前帧截图、VR；可选原生 mpv 播放 4K / HDR 与原盘 |
| WiFi 传文件 | 同一局域网内，手机扫码通过浏览器上传文件，电脑也可分享文件给手机下载 |
| MCP 与网页远程管理 | AI 客户端通过 MCP 创建媒体与 AI 任务、查询进度及读取结果；浏览器可浏览服务器文件、配置和下发任务，与桌面共用队列 |
| 任务队列与工具 | 并行处理、进度、停止、编辑参数、重试、日志、自动保存与导入导出；ZIP 压缩 / 解压、DVD / VOB 转换和原始数据复制为 ISO |

主窗口侧栏按视频、音频、图片、文档、工具集五类组织，光盘位于工具集内。拖入文件后，路由按文件类型显示匹配工具及带入 / 跳过数量，支持按类型勾选文件和键盘选择；选定工具并确认参数后，可立即执行或仅加入队列。快速剪辑先编辑片段，再统一配置导出，默认每个片段生成独立文件。

### 使用边界

- **Fast Copy** 的剪辑起点受关键帧限制；裁剪、画面旋转等滤镜需要重新编码。
- **HEIC / HEIF** 压缩只处理静态主图，输出 JPEG / WebP / PNG，不保留 Live Photo 视频、深度图或 HDR 增益图。
- **PDF → DOCX / XLSX** 提取文本，不重建原始排版；PDF 做旧和整页压缩会栅格化页面。
- **去除水印** 使用选区周围像素进行插值修复，不能还原被遮挡的真实内容；重新封装也不保证修复损坏媒体。
- **AI 与模型**：按所选功能下载所需模型；有限采样和模型输出可能遗漏或误判，字幕、标签、人物片段和总结需复核。线上 AI 会发送选定资料与采样画面。LaMa 当前仅有模型安装，修复推理尚未接入；启动时后台下载该模型默认开启，可在设置的本地模型页关闭。
- **平台能力**：硬件转码取决于驱动、编码器、容器及输入；原生 mpv 运行包首次使用时单独下载，应用自动下载的 macOS mpv 运行包要求 macOS 14 及以上。Windows 浏览器嗅探需要可用的 WebView2 Runtime。
- 下载可用性取决于站点、账号、地区和网络限制，详见 [视频下载](docs/VIDEO-DOWNLOAD.md)。

## MCP 与网页远程管理

在 **设置 → 远程管理** 启用服务并点击“应用”，默认端口为 `18920`：

- **网页工具箱**：打开 `http://127.0.0.1:18920/`，浏览服务器文件、选择工具与参数、提交任务，并查看进度、日志和输出。
- **MCP 客户端**：使用支持 Streamable HTTP 的 AI 客户端连接 `http://127.0.0.1:18920/mcp`，调用媒体转换、AI 标签分类、自动媒体整理、字幕和总结等工具。
- **局域网访问**：开启“允许局域网连接”并应用，再使用设置页列出的地址。服务不要求访问令牌，仅在可信网络中开启。

网页随应用打包，媒体处理和模型推理在运行 AvaMedia 的电脑上完成；文件路径指向该电脑，远程客户端不会自动上传本地文件。桌面、网页和 MCP 共用任务队列，关闭网页不取消任务，主窗口缩到托盘后服务继续运行。后台宿主仍需要桌面会话。连接配置、常驻启动及工具列表见 [MCP 使用说明](docs/MCP.md)，网页功能范围与待完成事项见 [网页视频功能对齐状态](docs/WEB-VIDEO-PARITY.md)。

## 界面截图

以下为 **2026-10-09、v1.1.51** macOS 开发构建的历史中文配图，当前主窗口、设置和工具布局已有调整。这些截图只展示当时窗口外观，不代表当前功能或其他平台已验收；示例画面来自 [NASA 公共领域照片](tests/AvaMedia.BatchRotateTests/Fixtures/README.md)。

| Light | Dark |
| --- | --- |
| [![AvaMedia 浅色主窗口与任务队列](docs/assets/screenshots/main-light.png)](docs/assets/screenshots/main-light.png) | [![AvaMedia 深色主窗口与任务队列](docs/assets/screenshots/main-dark.png)](docs/assets/screenshots/main-dark.png) |
| **Mac OS 9 · Platinum** | **Windows XP · Luna** |
| [![AvaMedia Platinum 主窗口](docs/assets/screenshots/main-macos9.png)](docs/assets/screenshots/main-macos9.png) | [![AvaMedia Windows XP 主窗口](docs/assets/screenshots/main-winxp.png)](docs/assets/screenshots/main-winxp.png) |
| **文件路由 · Windows XP** | **快速剪辑 · Dark** |
| [![文件路由：输入文件、兼容数量、分类连线与自适应工具网格](docs/assets/screenshots/route-winxp.png)](docs/assets/screenshots/route-winxp.png) | [![快速剪辑：时间区间、边界预览与片段编辑](docs/assets/screenshots/editor-dark.png)](docs/assets/screenshots/editor-dark.png) |
| **播放器 · Platinum** | **视频下载 · Light** |
| [![Platinum 播放器与紧凑播放控制栏](docs/assets/screenshots/player-macos9.png)](docs/assets/screenshots/player-macos9.png) | [![视频下载：链接、画质、字幕和登录选项](docs/assets/screenshots/download-light.png)](docs/assets/screenshots/download-light.png) |

点击配图查看原图。[完整截图目录](docs/assets/screenshots/README.md) 收录四套皮肤的 44 张截图，包含路由、设置、转换、编辑、导出、播放及组件预览；检查范围与修正记录见 [外观检查](docs/acceptance/APPEARANCE-REVIEW.md)。

## 从源码运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，版本选择见 [`global.json`](global.json)。以下步骤包含远程网页，需要 `wasm-tools` 工作负载；普通桌面构建不会自动发布网页资源。

```sh
git clone https://github.com/mcxen/AvaMedia.git
cd AvaMedia
dotnet workload install wasm-tools
dotnet publish src/AvaMedia.Browser/AvaMedia.Browser.csproj -c Release
dotnet build src/AvaMedia.Desktop/AvaMedia.Desktop.csproj -c Release
dotnet run --project src/AvaMedia.Desktop -c Release --no-build
```

源码运行需准备 FFmpeg / FFprobe 9.x；下载另需 yt-dlp，要求 JavaScript 运行时的站点还需 QuickJS-NG。可使用 Release 说明链接的媒体工具包。未内置工具的构建可在“设置 → 高级 → 外部工具”配置路径，或使用 `AVAMEDIA_FFMPEG`、`AVAMEDIA_FFPROBE`、`AVAMEDIA_YT-DLP`、`AVAMEDIA_QJS` 环境变量；查找也支持项目 `.tools`、PATH 和 macOS Homebrew 路径。Windows / macOS 安装包固定使用内置工具，内置文件缺失时提示重新安装完整版本。

只开发桌面工具或 MCP 时可省略工作负载安装和 Browser 发布；此时网页入口返回“网页界面未构建”。修改 Browser 后需重新发布它，再构建 Desktop，才能将新网页复制到运行目录。

需要隔离本地验收的设置、队列与任务日志时，可在启动进程前设置 `AVAMEDIA_STATE_DIRECTORY` 为独立目录；模型缓存与 AI 检查点仍共享。MCP 后台宿主也可使用 `--mcp-state-directory` 显式指定工作区。

日常源码修改只构建受影响项目并完成必要静态检查；仅修改文档或资源时不运行构建和测试。功能回归按明确要求执行，范围遵循 [`AGENTS.md`](AGENTS.md)。

### 代码结构

| 路径 | 职责 |
| --- | --- |
| [`src/AvaMedia.Core`](src/AvaMedia.Core) | 媒体模型、参数校验、任务计划、队列与媒体处理，不依赖 Avalonia |
| [`src/AvaMedia.Controls`](src/AvaMedia.Controls) | 通用控件、具名主题与动态资源契约 |
| [`src/AvaMedia.FileUi`](src/AvaMedia.FileUi) / [`src/AvaMedia.MediaUi`](src/AvaMedia.MediaUi) | 桌面与网页共用的文件浏览、路径输入和媒体编辑控件 |
| [`src/AvaMedia.Mcp`](src/AvaMedia.Mcp) | MCP、网页 HTTP 接口与服务器媒体预览，接入宿主共享队列 |
| [`src/AvaMedia.Desktop`](src/AvaMedia.Desktop) | 桌面窗口、语言、皮肤、预览、播放与平台集成 |
| [`src/AvaMedia.Browser`](src/AvaMedia.Browser) | Avalonia WebAssembly 远程管理客户端，发布资源随桌面客户端打包 |
| [`src/AvaMedia.Bootstrap`](src/AvaMedia.Bootstrap) | Windows / macOS 原生首次配置、运行时安装与应用启动 |
| [`website`](website) | 中英文宣传落地页、功能与界面展示；运行与构建见 [网页说明](website/README.md) |
| [`scripts`](scripts) / [`.github/workflows`](.github/workflows) | 媒体引擎准备、构建、打包与发布 |
| [`tests`](tests) | 专项验证程序与测试素材 |

接口边界见 [架构说明](docs/ARCHITECTURE.md)，品牌配置见 [`Branding.props`](Branding.props)，界面规范见 [`UISPEC.MD`](UISPEC.MD)。

## 文档与贡献

完整资料见 [文档索引](docs/README.md)，实际验证见 [验收记录](docs/acceptance/README.md)，历史发布见 [版本记录](docs/releases/README.md)。

| 主题 | 文档 |
| --- | --- |
| 转换与压缩 | [视频压缩](docs/VIDEO-COMPRESSION.md) · [视频瘦身](docs/VIDEO-SLIMMING.md) · [图片压缩](docs/IMAGE-COMPRESSION.md) · [HEIC](docs/HEIC.md) · [TS 视频](docs/TS-VIDEO.md) · [GPU 转码](docs/GPU-TRANSCODING.md) |
| 编辑与播放 | [快速剪辑](docs/QUICK-CLIP.md) · [视频编辑](docs/VIDEO-EDITING.md) · [批量裁剪](docs/BATCH-CROP.md) · [批量旋转](docs/BATCH-ROTATE.md) · [字幕与选轨](docs/SUBTITLE-OPTIONS.md) · [播放器](docs/PLAYER.md) |
| 文件与工具 | [文件路由](docs/MEDIA-ROUTING.md) · [WiFi 传文件](docs/WIFI-TRANSFER.md) · [天池看图](docs/IMAGE-VIEWER.md) · [PDF 工作区](docs/PDF-WORKSPACE.md) · [视频下载](docs/VIDEO-DOWNLOAD.md) · [批量工具](docs/BATCH-TOOLS.md) |
| AI 与模型 | [自动标签分类](docs/MEDIA-AI.md) · [自动媒体整理](docs/FOLDER-CLASSIFICATION.md) · [视频总结](docs/VIDEO-SUMMARY.md) · [有人片段](docs/PERSON-CLIP.md) · [字幕与语音](docs/SPEECH-TOOLS.md) · [模型管理](docs/MODELS.md) |
| 远程管理 | [MCP 与网页工具箱](docs/MCP.md) · [网页视频功能对齐状态](docs/WEB-VIDEO-PARITY.md) |
| 开发与发布 | [架构](docs/ARCHITECTURE.md) · [设置](docs/SETTINGS.md) · [Mac OS 9](docs/MACOS9-SKIN.md) · [Windows XP](docs/WINDOWS-XP-SKIN.md) · [Windows / macOS](docs/PLATFORMS.md) · [Linux](docs/LINUX.md) · [Git 工作流](docs/GIT-WORKFLOW.md) · [自动发布](docs/RELEASE.md) |

欢迎通过 [Issues](https://github.com/mcxen/AvaMedia/issues) 报告问题或提出建议。媒体相关问题请附系统版本、应用版本、输入格式、操作步骤和错误日志。

发布由 `vMAJOR.MINOR.PATCH` tag 触发。日常提交照常推送；相对上次已发布 tag 的有效代码变更累计达到 **1200 行**且相关本地检查通过后，才创建新版本 tag。文档、许可证、资源和生成文件不计入，已发布 tag 保持不变。具体流程见 [Git 工作流](docs/GIT-WORKFLOW.md)。

## 许可证

版权所有 © 2026 AvaMedia contributors。原创代码、测试、文档和原创图标采用 **AGPL-3.0-only**，见 [LICENSE](LICENSE) 与 [COPYRIGHT](COPYRIGHT)。第三方依赖、模型及安装器声明见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md) 和 [`licenses`](licenses)。

经典转换布局与编辑流程参考 FormatFactory，播放器操作参考 PotPlayer；未导入这两个产品的专有 DLL、图标或品牌素材。项目通用控件自行实现，Lucide 等第三方资源按其各自许可证分发。Windows / macOS 内置 FFmpeg 通过独立进程调用，包含 x264 / x265，采用 GPL-3.0-or-later；精确对应的源码和构建配方通过 Release 说明链接的媒体归档提供。
