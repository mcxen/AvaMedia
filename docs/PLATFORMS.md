# Windows 与 macOS

当前发布目标为 **Windows x64** 和 **macOS Apple Silicon（ARM64）**。安装包从项目首页的 Release 入口下载；构建与工具版本以 [发布流程](RELEASE.md) 和对应包内清单为准。

## 安装包与运行时

- Windows：portable ZIP 或每用户 setup 安装包。
- macOS：包含 `AvaMedia.app` 的 ARM64 DMG；最低系统版本声明为 **macOS 13.4**。应用使用 ad-hoc 签名，尚未进行 Developer ID 签名和公证。
- 当前应用包不包含 .NET。首次启动缺少可用的 .NET 8／ASP.NET Core 运行时时，由原生启动器下载、校验并安装到当前用户缓存，再启动应用；无需开发 SDK 或管理员权限。

macOS 13.4 的下限来自打包 ONNX Runtime ARM64 原生库的部署要求。QuickJS 从固定源码构建并指定 13.4 部署目标；归档检查核对依赖实际部署版本。该声明不代表已经完成 macOS 13.4 设备上的全部功能验收。

## 工具与平台行为

新构建内置 FFmpeg／FFprobe **9.0.2**、yt-dlp 和 QuickJS-NG。FFmpeg 版本来自共用的 [源码锁](../scripts/macos/ffmpeg-sources.lock.json)，各平台打包引擎、动态库、版本清单和许可证；精确对应源码由 Release 链接的媒体归档提供。见 [FFmpeg 配方](FFMPEG-MACOS.md)。

工具查找支持配置路径、环境变量、应用 `tools`、用户工具目录、项目工具目录、PATH 和 macOS Homebrew 路径；用户指定路径优先。开发构建需要自行准备工具，安装包直接使用内置工具。

HEIC／HEIF 可进入图片路由、压缩、转换、缩放／旋转及图片合成 PDF。macOS 信息读取与支持的静态图片预览使用系统 ImageIO；压缩编码沿用 FFmpeg。分块 HEIC 需要所用 FFmpeg 支持 Tile Grid、`stream_groups` 与 `xstack`，见 [HEIC](HEIC.md)。

Windows 内嵌声音使用 WaveOut，macOS 使用 AudioQueue；播放器与编辑器共享轨道选择、定位、静音及停止流程。高分辨率／HDR 还可使用独立 mpv 原生窗口，见 [原生播放](PLAYER-NATIVE.md)。设备听感、回调及音画同步的覆盖以具体验收记录为准。

文件定位使用 Windows Explorer 或 macOS Finder 的 `open -R`。Mac 默认输出目录为用户 Movies/AvaMedia，工具与预览缓存位于用户目录。大小写敏感卷、路径权限、Finder、字体、Retina 和设备能力应按实际环境核对。

当前开发版已移除屏幕录制及采集代码，Mac 应用不再声明摄像头与麦克风采集权限。

## 开发检查与历史证据

日常只检查受影响项目和必要静态诊断，执行范围遵循 [AGENTS.md](../AGENTS.md)。打包入口为：

```powershell
./scripts/Publish.ps1 -Runtime win-x64
./scripts/Publish.ps1 -Runtime osx-arm64
```

归档检查与原生运行各自记录结果；构建通过不能替代设备操作或媒体输出验收。真实模型、原生窗口、VR、WebView 和媒体验证见 [验收索引](acceptance/README.md)。

早期 [v1.0.5 验收](releases/1.0.5-verified.json) 包含 Windows 测试、安装、启动／卸载和 macOS 两种架构 runner 的原生启动。当时的包包含 .NET、提供 Intel Mac 架构，与当前打包方式不同；记录保留作历史追溯。后续 macOS 验收以 ARM64 为目标，未覆盖的设备及最低系统版本不从这些历史结果推定。
