# Windows 与 macOS

维护日期：2026-10-06。[v1.0.5 Release](https://github.com/mcxen/AvaMedia/releases/tag/v1.0.5) 已完成 Windows 全部 18 套测试、安装、原生启动和卸载验证；macOS 两个架构已在对应 GitHub runner 上完成接口、皮肤与发布应用原生启动验证。设备声音、录屏权限、Retina 与 Finder 交互仍需设备验收。记录见 [1.0.5 验收记录](releases/1.0.5-verified.json)。

后续 macOS 发布仅提供 Apple Silicon（ARM64），使用 ARM64 runner 完成构建与启动验证。v1.0.5 的两个架构包和验收记录保留为历史结果。

## v1.0.5 应用包

- Windows x64：`AvaMedia-1.0.5-win-x64-setup.exe` 每用户安装，或 ZIP 解压运行 `AvaMedia.Desktop.exe`。
- Apple Silicon：`AvaMedia-1.0.5-osx-arm64.pkg` / `.dmg` / `.zip`，包含 `AvaMedia.app`。
- Intel Mac：`AvaMedia-1.0.5-osx-x64.pkg` / `.dmg` / `.zip`，包含 `AvaMedia.app`。

应用包含 .NET 运行时，不要求用户安装开发 SDK。macOS 包含原生 apphost、Avalonia/Skia/HarfBuzz dylib、Info.plist、许可与说明；ZIP 保存 Unix 创建平台和执行权限。构建方式依据 [Avalonia macOS 部署说明](https://docs.avaloniaui.net/docs/deployment/macos)。Mac 包使用 ad-hoc 签名，尚未进行 Developer ID 签名和公证；初次安装的系统放行和最低系统版本仍需设备验收。

## 工具与平台行为

新构建内置官方 yt-dlp 和 QuickJS-NG（Windows x64 / macOS ARM64），保持用户指定工具路径优先。FFmpeg / FFprobe 是独立媒体引擎，Windows 使用 `scripts/Install-MediaTools.ps1`。macOS 在终端运行应用资源中的 `scripts/Install-MediaTools-macOS.sh`，下载并核对定制 ARM64 FFmpeg 运行包，在 `~/Library/Application Support/AvaMedia/tools` 创建链接；安装 FFmpeg 无需 Homebrew。定制运行包和对应源码独立发布，见 [macOS FFmpeg 配方与验证](FFMPEG-MACOS.md)。旧 v1.0.5 包仍使用原有外部工具配置。

工具查找支持配置路径、环境变量、Mac 应用资源的 tools、用户工具目录、Homebrew 的 `/opt/homebrew/bin` 或 `/usr/local/bin`、项目工具目录和 PATH。Finder 启动应用时也可找到 Homebrew 工具。

Windows 声音使用 WaveOut；macOS 声音接入系统 AudioToolbox 的 AudioQueue，读取共享的 16 位 PCM 缓存。轨道选择、定位、静音与停止使用同一套播放流程，音频设备错误会显示在预览中。实现依据 [Apple Audio Queue Services](https://developer.apple.com/documentation/audiotoolbox/audio-queue-services)；Mac 听感、停止回调及音画同步尚待真机验证。

录屏在 Windows 使用 gdigrab，在 macOS 使用 [FFmpeg AVFoundation](https://ffmpeg.org/ffmpeg-devices.html#avfoundation)。Mac 可自动选择首个屏幕或刷新并选择屏幕设备；拒绝把摄像头当作屏幕。录制包含鼠标，尚不包含系统声音。首次录制需系统屏幕录制权限，失败时保留工具日志并提示权限入口。停止保留已有片段。

文件定位在 Windows 使用 Explorer，在 macOS 使用 Finder 的 `open -R`。Mac 默认输出目录为用户 Movies/AvaMedia，工具与预览缓存独立于应用包；这些路径的真实权限、大小写卷及升级行为仍需 Mac 验收。

## 开发与验收

```powershell
./scripts/Publish.ps1 -Runtime win-x64
./scripts/Publish.ps1 -Runtime osx-arm64
./scripts/Verify-MacPackages.ps1
```

Mac 包检查通过 64 项：两个架构的八种关键 Mach-O 二进制（含 .NET CoreCLR、hostpolicy 与 ONNX Runtime）、执行权限、Info.plist、应用 ICNS 图标、工具脚本、许可和 ZIP 元数据。该脚本只检查归档，原生运行验证由 [Release 工作流](https://github.com/mcxen/AvaMedia/actions/runs/37384638311) 在两种架构 runner 完成。发布包哈希和验证范围保存在 [验收记录](releases/1.0.5-verified.json)。

后续 Mac 真机验收以 Apple Silicon（ARM64）为目标，需记录 OS、CPU、FFmpeg 版本，运行共同的字幕/音频/转换样例，并实测窗口、声音、定位/静音/停止、录屏权限允许与拒绝、Finder、中文文件名与字幕、系统动效、文档中文字体。完成前不宣称设备验收已完成。
