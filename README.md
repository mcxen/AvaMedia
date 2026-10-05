# AvaMedia

Avalonia 11.3.22 + C# / .NET 8 桌面多媒体客户端。经典布局和剪辑工作流参考 FormatFactory X64 5.10.0；客户端源代码与矢量图标独立实现，没有导入原产品的专有 DLL、图标或品牌素材。

## 运行

本机成品：`artifacts/release/1.0.2/win-x64/AvaMedia.Desktop.exe`。也可双击根目录 `Start-AvaMedia.cmd`。支持 Windows x64，不需要另外安装 .NET。

转换引擎使用外部 FFmpeg / FFprobe；下载使用可选 yt-dlp。本工作区已在 `.tools` 配置独立 LGPL FFmpeg 和 yt-dlp，可以直接运行。成品 ZIP 不包含这些外部工具。移到另一台电脑后，可运行 `scripts/Install-MediaTools.ps1` 安装工具到独立目录，或在“选项”里指定已安装的工具路径。也可设置 `AVAMEDIA_FFMPEG`、`AVAMEDIA_FFPROBE`、`AVAMEDIA_YT-DLP` 环境变量。

选中格式 → 添加文件 → 配置输出 → 确定 → 点击开始。通过右键任务编辑、重试、查看日志、打开输出。支持拖入文件、批量转换、并行队列、自动保存和导入导出。输出名称发生冲突时生成新名字，源文件不被覆盖。

“快速剪辑”使用独立文件列表，默认 Fast Copy，可选择 MP4/MKV。缩略图或每行“选项”打开编辑器，每个文件独立保存剪辑区间。支持媒体信息、源目录、上下移动、排序、移除、等分片段、设置名称和输出到源目录。Fast Copy 保留源容器和编码，复制首个视频及音轨，起点受关键帧限制；需要精确边界或画面/音频滤镜时选择 MP4/MKV 重新编码。接受滤镜后会自动切换为 MP4 并显示原因。详见 [docs/QUICK-CLIP.md](docs/QUICK-CLIP.md)。

“画面裁剪 / 去除水印”添加媒体后点击“选项 / 剪辑”；可拖动时间范围和裁剪框，设置边界、精度、速度及淡入淡出。播放器在窗口内通过 FFmpeg 解码，Windows 音频由 NAudio 播放。

## 构建和验证

```powershell
& .\.tools\dotnet\dotnet.exe build AvaMedia.sln -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.SmokeTests
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.QuickClipTests -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.FunctionTests -c Release
& .\.tools\dotnet\dotnet.exe run --project tests/AvaMedia.AudioOptionsTests -c Release
& .\scripts\Publish.ps1
```

没有本地 SDK 时，使用官方 dotnet-install 脚本或安装 .NET 8 SDK。工程内的 SDK 不改变全局开发环境。

测试生成自己的媒体数据，包括带中文、空格和单引号的文件名、不同画面尺寸以及无音轨的视频。验证实际生成的媒体与文档、取消任务、恢复队列、错误隔离、下载及录屏，不需要用户的媒体文件。

开发时可用 `AvaMedia.Desktop.exe --capture <目录> --editor <视频路径> --verify-ui` 生成主窗口与编辑器 PNG，并检查实际播放解码、剪辑边界及裁剪控件。追加 `--quick-clip <视频路径>` 可生成快速剪辑列表 PNG。

## 功能与验证

经典主界面、快速剪辑列表、播放器、输出配置和批量工具均已接到底层。52 个转换入口均有实际输出验证，涵盖音视频、图片、PDF、ZIP、下载、录屏和原始 ISO 数据复制。另有逐文件剪辑、合并时间区间、字幕特殊路径、采样率、声道、音效、质量、预设、主题与按钮操作检查。详细记录见 [docs/FEATURES.md](docs/FEATURES.md)，技术与协议调研见 [docs/RESEARCH.md](docs/RESEARCH.md)。

预览显示原媒体，编辑效果应用于输出。PDF → Office 提取文本；水印区域使用模糊处理；录屏采集 Windows 桌面画面。硬件编码使用本机驱动及配置的 FFmpeg。

## 许可证

原创代码与图标采用 MIT。依赖声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、`licenses/` 和自动生成的 `licenses/dependencies.json`。

FFmpeg 没有被链接到客户端或打包进成品；不同构建可能适用 LGPL/GPL 或不可再分发条款。当前开发构建不启用 GPL / nonfree，且启用 version3，须按其实际 LGPL 版本处理。发布自带引擎的安装包时应提供**精确对应的源代码、构建配置、修改说明及所有组件版权许可**，不能仅附上项目首页。完整要求参考 FFmpeg 官方法律说明。
