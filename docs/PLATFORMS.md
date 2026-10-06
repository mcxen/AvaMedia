# Windows 与 macOS

维护日期：2026-10-06。[v1.0.5 Release](https://github.com/mcxen/AvaMedia/releases/tag/v1.0.5) 已完成 Windows 全部 18 套测试、安装、原生启动和卸载验证；macOS 两个架构已在对应 GitHub runner 上完成接口、皮肤与发布应用原生启动验证。设备声音、Retina 与 Finder 交互仍需设备验收。记录见 [1.0.5 验收记录](releases/1.0.5-verified.json)。

后续 macOS 发布仅提供 Apple Silicon（ARM64），使用 ARM64 runner 完成构建与启动验证。v1.0.5 的两个架构包和验收记录保留为历史结果。

当前应用最低系统版本声明为 **macOS 13.4**：打包的 ONNX Runtime 1.23.2 ARM64 原生库在 `LC_BUILD_VERSION` 中要求 13.4；.NET、Avalonia、Skia 与 HarfBuzz 组件要求 11.0，独立 FFmpeg 配方的部署目标为 12.0。QuickJS 官方 ARM64 二进制要求 macOS 26，已改为从同版本固定源码构建、指定 13.4 部署目标。归档检查同时核对组件和下载工具的实际部署版本，避免旧系统在加载人脸识别或解析下载链接时暴露依赖错误。此声明与配方仍须通过原生构建，macOS 13.4 的完整设备运行仍需验收。

## v1.0.5 应用包

- Windows x64：`AvaMedia-1.0.5-win-x64-setup.exe` 每用户安装，或 ZIP 解压运行 `AvaMedia.Desktop.exe`。
- Apple Silicon：`AvaMedia-1.0.5-osx-arm64.pkg` / `.dmg` / `.zip`，包含 `AvaMedia.app`。
- Intel Mac：`AvaMedia-1.0.5-osx-x64.pkg` / `.dmg` / `.zip`，包含 `AvaMedia.app`。

应用包含 .NET 运行时，不要求用户安装开发 SDK。macOS 包含原生 apphost、Avalonia/Skia/HarfBuzz dylib、Info.plist、许可与说明；ZIP 保存 Unix 创建平台和执行权限。构建方式依据 [Avalonia macOS 部署说明](https://docs.avaloniaui.net/docs/deployment/macos)。Mac 包使用 ad-hoc 签名，尚未进行 Developer ID 签名和公证；初次安装的系统放行和最低系统版本仍需设备验收。

## 工具与平台行为

新构建在 Windows portable / setup 和 macOS DMG 中内置 FFmpeg / FFprobe 8.1.3、官方 yt-dlp 和 QuickJS-NG。配置留空时直接使用应用 `tools` 中的引擎，无需再次安装；用户指定路径和环境变量仍优先。引擎及动态库、版本清单、组件许可证一并打包，对应源码保存在主 Release 链接的媒体归档页。见 [macOS FFmpeg 配方与验证](FFMPEG-MACOS.md) 和 [自动发布](RELEASE.md)。

工具查找支持配置路径、环境变量、Mac 应用资源的 tools、用户工具目录、Homebrew 的 `/opt/homebrew/bin` 或 `/usr/local/bin`、项目工具目录和 PATH。Finder 启动应用时也可找到 Homebrew 工具。

HEIC / HEIF 可进入图片路由、压缩、转换、缩放 / 旋转与图片合成 PDF。macOS 信息读取及不填边的 HEIC / JPEG / PNG / TIFF 预览使用系统 ImageIO，无需启动媒体进程；压缩编码仍由 FFmpeg 完成。分块 HEIC 需要 FFprobe 的 stream_groups 信息与 FFmpeg 8.1 系列的 Tile Grid 合成能力（含 `xstack`），定制 ARM64 引擎固定 8.1.3 并检查该滤镜，Windows 安装脚本使用含这些能力的当前构建。自行指定旧引擎时需升级到支持上述能力的版本。当前 Windows 主机只完成源码编译和静态检查，ImageIO 的真实运行与 Apple Silicon 性能仍待实机验证；实现见 [HEIC](HEIC.md)。

Windows 声音使用 WaveOut；macOS 声音接入系统 AudioToolbox 的 AudioQueue，读取共享的 16 位 PCM 缓存。轨道选择、定位、静音与停止使用同一套播放流程，音频设备错误会显示在预览中。实现依据 [Apple Audio Queue Services](https://developer.apple.com/documentation/audiotoolbox/audio-queue-services)；Mac 听感、停止回调及音画同步尚待真机验证。

当前开发版已移除屏幕录制功能及采集代码；Mac 应用不再声明摄像头与麦克风采集权限。

文件定位在 Windows 使用 Explorer，在 macOS 使用 Finder 的 `open -R`。Mac 默认输出目录为用户 Movies/AvaMedia，工具与预览缓存独立于应用包；这些路径的真实权限、大小写卷及升级行为仍需 Mac 验收。

## 开发与验收

```powershell
./scripts/Publish.ps1 -Runtime win-x64
./scripts/Publish.ps1 -Runtime osx-arm64
./scripts/Verify-MacPackages.ps1
```

Mac 包检查通过 64 项：两个架构的八种关键 Mach-O 二进制（含 .NET CoreCLR、hostpolicy 与 ONNX Runtime）、执行权限、Info.plist、应用 ICNS 图标、工具脚本、许可和 ZIP 元数据。该脚本只检查归档，原生运行验证由 [Release 工作流](https://github.com/mcxen/AvaMedia/actions/runs/37384638311) 在两种架构 runner 完成。发布包哈希和验证范围保存在 [验收记录](releases/1.0.5-verified.json)。

后续 Mac 真机验收以 Apple Silicon（ARM64）为目标，需记录 OS、CPU、FFmpeg 版本，运行共同的字幕/音频/转换样例，并实测窗口、声音、定位/静音/停止、Finder、中文文件名与字幕、系统动效、文档中文字体。完成前不宣称设备验收已完成。
