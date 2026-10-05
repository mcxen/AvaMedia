# Windows 与 macOS

维护日期：2026-10-05。Windows 已运行测试；macOS 已完成平台代码接入、两个架构交叉发布和包检查，尚未在 Mac 真机启动或验证声音、录屏、Retina 与 Finder。

## 应用包

- Windows x64：`artifacts/AvaMedia-1.0.2-win-x64.zip`，解压运行 `AvaMedia.Desktop.exe`。
- Apple Silicon：`artifacts/AvaMedia-1.0.2-osx-arm64.zip`，包含 `AvaMedia.app`。
- Intel Mac：`artifacts/AvaMedia-1.0.2-osx-x64.zip`，包含 `AvaMedia.app`。

应用包含 .NET 运行时，不要求用户安装开发 SDK。macOS 包含原生 apphost、Avalonia/Skia/HarfBuzz dylib、Info.plist、许可与说明；ZIP 保存 Unix 创建平台和执行权限。构建方式依据 [Avalonia macOS 部署说明](https://docs.avaloniaui.net/docs/deployment/macos)。当前 Mac 包未使用 Developer ID 签名或公证，分发与首次启动仍需在 Mac 上验证；最低系统版本也尚未验收。

## 工具与平台行为

FFmpeg、FFprobe、yt-dlp 均为外部可替换工具，没有打包进应用。Windows 使用 `scripts/Install-MediaTools.ps1`。macOS 安装好 Homebrew 后，在终端运行应用资源中的 `scripts/Install-MediaTools-macOS.sh`；它安装 FFmpeg 与 yt-dlp，并在 `~/Library/Application Support/AvaMedia/tools` 创建链接。Homebrew 所选 FFmpeg 配置可能与 Windows 开发用的 LGPL 构建不同，应以实际构建信息为准。

工具查找支持配置路径、环境变量、Mac 应用资源的 tools、用户工具目录、Homebrew 的 `/opt/homebrew/bin` 或 `/usr/local/bin`、项目工具目录和 PATH。Finder 启动应用时也可找到 Homebrew 工具。

Windows 声音使用 WaveOut；macOS 声音接入系统 AudioToolbox 的 AudioQueue，读取共享的 16 位 PCM 缓存。轨道选择、定位、静音与停止使用同一套播放流程，音频设备错误会显示在预览中。实现依据 [Apple Audio Queue Services](https://developer.apple.com/documentation/audiotoolbox/audio-queue-services)；Mac 听感、停止回调及音画同步尚待真机验证。

录屏在 Windows 使用 gdigrab，在 macOS 使用 [FFmpeg AVFoundation](https://ffmpeg.org/ffmpeg-devices.html#avfoundation)。Mac 可自动选择首个屏幕或刷新并选择屏幕设备；拒绝把摄像头当作屏幕。录制包含鼠标，尚不包含系统声音。首次录制需系统屏幕录制权限，失败时保留工具日志并提示权限入口。停止保留已有片段。

文件定位在 Windows 使用 Explorer，在 macOS 使用 Finder 的 `open -R`。Mac 默认输出目录为用户 Movies/AvaMedia，工具与预览缓存独立于应用包；这些路径的真实权限、大小写卷及升级行为仍需 Mac 验收。

## 开发与验收

```powershell
./scripts/Publish.ps1 -Runtime win-x64
./scripts/Publish.ps1 -Runtime osx-arm64
./scripts/Publish.ps1 -Runtime osx-x64
./scripts/Verify-MacPackages.ps1
```

Mac 包检查通过 40 项：两个架构的五种关键 Mach-O 二进制、执行权限、Info.plist、工具脚本、许可和 ZIP 元数据。报告：[包检查报告](../artifacts/mac-packages-20261005-200910/report.json)。该报告明确将 `macOSRuntimeVerified` 记录为 false。

后续 Mac 真机验收需记录 OS、CPU、FFmpeg 版本，运行共同的字幕/音频/转换样例，并实测窗口、声音、定位/静音/停止、录屏权限允许与拒绝、Finder、中文文件名与字幕、系统动效、文档中文字体。Apple Silicon 和 Intel 分别保存结果；完成前不宣称双平台交付已验收。
