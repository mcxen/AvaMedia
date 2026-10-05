# AvaMedia

Avalonia 11.3.22 + C# / .NET 8 桌面多媒体客户端。经典布局和剪辑工作流参考 FormatFactory X64 5.10.0；客户端源代码与矢量图标独立实现，没有导入原产品的专有 DLL、图标或品牌素材。

## 运行

安装 .NET 8 SDK 后，从源码启动：

```sh
git clone https://github.com/mcxen/AvaMedia.git
cd AvaMedia
dotnet restore AvaMedia.sln
dotnet build AvaMedia.sln -c Release
dotnet run --project src/AvaMedia.Desktop -c Release
```

Windows 可运行 `powershell -File scripts/Install-MediaTools.ps1` 安装独立媒体工具；macOS 使用 `bash scripts/Install-MediaTools-macOS.sh`。工具安装与平台说明见 [docs/PLATFORMS.md](docs/PLATFORMS.md)。

本机 Windows x64 成品：`artifacts/release/1.0.2/win-x64/AvaMedia.Desktop.exe`，也可双击 `Start-AvaMedia.cmd`。macOS Apple Silicon / Intel 的 `.app` 预览包分别在 `artifacts/AvaMedia-1.0.2-osx-arm64.zip` 和 `artifacts/AvaMedia-1.0.2-osx-x64.zip`。包均包含运行时；Mac 平台代码和交叉发布已完成，真机启动与媒体能力尚待验收。说明见 [docs/PLATFORMS.md](docs/PLATFORMS.md)。

转换引擎使用外部 FFmpeg / FFprobe；下载使用可选 yt-dlp。本工作区已在 `.tools` 配置独立 LGPL FFmpeg 和 yt-dlp，可以直接运行。成品 ZIP 不包含这些外部工具。移到另一台电脑后，可运行 `scripts/Install-MediaTools.ps1` 安装工具到独立目录，或在“选项”里指定已安装的工具路径。也可设置 `AVAMEDIA_FFMPEG`、`AVAMEDIA_FFPROBE`、`AVAMEDIA_YT-DLP` 环境变量。

macOS 工具安装使用 `scripts/Install-MediaTools-macOS.sh`，通过已安装的 Homebrew 配置 FFmpeg 和 yt-dlp；应用支持从 Finder 启动后查找 Homebrew 或用户工具目录。

选中格式 → 添加文件 → 配置输出 → 确定 → 点击开始。通过右键任务编辑、重试、查看日志、打开输出。支持拖入文件、批量转换、并行队列、自动保存和导入导出。输出名称发生冲突时生成新名字，源文件不被覆盖。

“快速剪辑”使用独立文件列表，默认 Fast Copy，可选择 MP4/MKV。缩略图或每行“选项”打开编辑器，每个文件独立保存剪辑区间。支持媒体信息、源目录、上下移动、排序、移除、等分片段、设置名称和输出到源目录。Fast Copy 保留源容器和编码，复制首个视频及音轨，起点受关键帧限制；需要精确边界或画面/音频滤镜时选择 MP4/MKV 重新编码。接受滤镜后会自动切换为 MP4 并显示原因。详见 [docs/QUICK-CLIP.md](docs/QUICK-CLIP.md)。

“画面裁剪”支持多选视频并共用一个选区。默认采用相同像素坐标，也可按画面比例应用到不同分辨率的视频；预览拖框或输入坐标后，批量加入队列并点击开始。详见 [docs/BATCH-CROP.md](docs/BATCH-CROP.md)。

“去除水印”添加媒体后点击“选项 / 剪辑”；可拖动时间范围和区域框，设置边界、精度、速度及淡入淡出。播放器通过 FFmpeg 解码，Windows 声音使用 WaveOut，Mac 声音接入 AudioQueue。输出配置可选择视频/音频轨和字幕处理方式，详见 [字幕与选轨](docs/SUBTITLE-OPTIONS.md)。

外观可从菜单 皮肤 → Mac OS 9 · Platinum 切换为经典 Mac 灰色立体控件与条纹标题栏，选择会保存。设计与开源说明见 [docs/MACOS9-SKIN.md](docs/MACOS9-SKIN.md)。

## 构建和验证

克隆后的标准验证命令：

```powershell
dotnet run --project tests/AvaMedia.SkinTests -c Release
powershell -File scripts/Verify.ps1
```

下面的 .tools 命令适用于已在工程内配置本地 SDK 的开发环境。

```powershell
& .\.tools\dotnet\dotnet.exe build AvaMedia.sln -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.SmokeTests
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.QuickClipTests -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.BatchCropTests -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.FunctionTests -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.AudioOptionsTests -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.SubtitleTests -c Release
& .\scripts\Publish.ps1
```

没有本地 SDK 时，使用官方 dotnet-install 脚本或安装 .NET 8 SDK。工程内的 SDK 不改变全局开发环境。

测试生成自己的媒体数据，包括带中文、空格和单引号的文件名、不同画面尺寸以及无音轨的视频。验证实际生成的媒体与文档、取消任务、恢复队列、错误隔离、下载及录屏，不需要用户的媒体文件。

开发时可用 `AvaMedia.Desktop.exe --capture <目录> --editor <视频路径> --verify-ui` 生成主窗口与编辑器 PNG，并检查实际播放解码、剪辑边界及裁剪控件。追加 `--quick-clip <视频路径>` 可生成快速剪辑列表 PNG。

## 功能与验证

经典主界面、快速剪辑列表、播放器、输出配置和批量工具均已接到底层。52 个转换入口均有实际输出验证，涵盖音视频、图片、PDF、ZIP、下载、录屏和原始 ISO 数据复制。另有逐文件剪辑、合并时间区间、字幕特殊路径、采样率、声道、音效、质量、预设、主题与按钮操作检查。详细记录见 [docs/FEATURES.md](docs/FEATURES.md)，技术与协议调研见 [docs/RESEARCH.md](docs/RESEARCH.md)。

预览显示所选原媒体轨道，编辑效果应用于输出。PDF → Office 提取文本；水印区域使用模糊处理；录屏采集屏幕画面。硬件编码使用本机驱动及配置的 FFmpeg。Windows 实际功能已验证，Mac 仍须完成真机验收。

Git 管理和统一验证入口见 [docs/GIT-WORKFLOW.md](docs/GIT-WORKFLOW.md)，媒体处理的 18 组消融对照见 [docs/ABLATION.md](docs/ABLATION.md)。

## 许可证

版权所有 © 2026 AvaMedia contributors。原创代码、测试、文档与矢量图标采用 **AGPL-3.0-only**，许可全文见 [LICENSE](LICENSE)，版权说明见 [COPYRIGHT](COPYRIGHT)。依赖声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、`licenses/` 和自动生成的 `licenses/dependencies.json`。

FFmpeg 没有被链接到客户端或打包进成品；不同构建可能适用 LGPL/GPL 或不可再分发条款。当前开发构建不启用 GPL / nonfree，且启用 version3，须按其实际 LGPL 版本处理。发布自带引擎的安装包时应提供**精确对应的源代码、构建配置、修改说明及所有组件版权许可**，不能仅附上项目首页。完整要求参考 FFmpeg 官方法律说明。
