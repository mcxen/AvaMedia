# 自动发布

日常 CI 仅在客户端源码、界面模板、项目或依赖配置改变时编译 `AvaMedia.Desktop` 及其 Core 依赖，不编译测试项目、不下载 FFmpeg、不运行媒体、截图或消融回归。只改文档、图片等资源时跳过。需要全量回归时，在 CI 的 **Run workflow** 中勾选 `full_verification`；该模式独立运行原有 `scripts/Verify.ps1`，不会先重复编译客户端。

Release 工作流接收 `vMAJOR.MINOR.PATCH` tag，依次执行：

1. Windows 媒体引擎与 macOS 并行准备；Windows 从固定源码交叉构建 x64 FFmpeg，macOS 复用或构建验证过的 ARM64 引擎。`Publish.ps1` 执行各平台所需的客户端 Release 编译，并把对应引擎及动态库复制进应用 `tools`。
2. 发布包含 .NET 运行时的 Windows x64 ZIP；以固定版本 Inno Setup 6.4.3 生成每用户安装程序，实际验证安装、版本、原生启动、许可证和卸载。
3. 在 Apple Silicon runner 构建包含 FFmpeg、FFprobe、动态库及 QuickJS 的 macOS ARM64 应用 ZIP、PKG 与 DMG，检查包内引擎加载并启动客户端。Windows 安装检查核对内置引擎文件哈希及版本。Windows 失败不会跳过 macOS 打包；两者均通过后才发布。
4. 检查 Mac ARM64 ZIP 的 Mach-O 架构和 Unix 执行权限，生成对应源码 ZIP、SHA256SUMS.txt，自动创建 GitHub Release 并上传。

必要步骤失败时不发布 Release。构建 job 使用 `contents: read`，上传 job 单独获得 `contents: write` 和 GitHub 自带的 `GITHUB_TOKEN`；Actions 固定到核对过的 SHA。同一个 tag 可手动重跑，成品会重新上传。后续版本使用新 tag，不移动已发布 tag。

2026-10-06 的 [v1.1.8 发布失败日志](https://github.com/mcxen/AvaMedia/actions/runs/37453172312) 有两个独立失败点：Windows `FunctionTests` 按已移除的“批量”菜单标题查找入口，现改用 `BatchCropMenuItem` 控件名称；macOS FriBidi 构建缺少 `help2man`，Release 和独立 FFmpeg 工作流现均安装该工具。旧 tag 保留原提交，重跑旧记录仍使用原工作流与源码；这些修复随后续版本 tag 生效。

后续原生 CI 的三个阻塞已修复：LAME 4.0 编码库关闭不需要的 mpg123 解码器；FFmpeg 显式链接 macOS 系统 libiconv；ICO 输出验证指定 PNG 所需的 RGBA 像素格式。[提交 18b61fc 的完整原生 CI](https://github.com/mcxen/AvaMedia/actions/runs/37471073518) 已通过，包含 225 项检查、27 个真实媒体输出、归档安装及上传，原生缓存保存成功。[Windows 客户端 CI](https://github.com/mcxen/AvaMedia/actions/runs/37459158475) 也已通过；这次仅修改 macOS 构建与验证脚本。该记录验证媒体工具工作流；客户端安装包以各版本的 Release 记录为准。

## 发布提速

Release 和独立 macOS 工作流共用 `native-media` action，缓存验证通过的 FFmpeg 运行包、精确对应的源码包、校验清单及 QuickJS。缓存按 ARM64、Clang / SDK、源码锁文件、构建与验证脚本、能力清单和许可证内容精确匹配；普通客户端修改与版本号变更可以复用，相关输入变化则重建。不使用模糊匹配的原生运行包。缓存命中后仍核对两个 FFmpeg 归档的 SHA256、配方与源码锁哈希，重新执行原有原生媒体验证及 QuickJS 检查；客户端、安装器和发布检查保留。运行包与对应源码在原生验证成功后立即写入缓存，后续客户端失败不会丢失它们。默认分支的缓存可被 tag 工作流读取，首次构建或缓存淘汰时仍需完整编译。依据 [GitHub Cache 文档](https://github.com/actions/cache#cache-scopes)。

客户端构建 job 缓存 NuGet 下载包，仍由正常 restore / build 解析当前依赖；不缓存客户端 `bin`、`obj` 或测试通过结果。日常 CI 与独立 macOS 工作流取消同一分支被新提交替代的旧运行；Release 按 tag 保留。ZIP、安装器及 FFmpeg 压缩包上传设置 `compression-level: 0`，避免二次压缩；验证日志单独保存，发布 job 只下载成品。依据 [Artifact 压缩说明](https://github.com/actions/upload-artifact#altering-compressions-level-speed-v-size)。

独立 FFmpeg 工作流只处理媒体工具、源码和 QuickJS；仅当原生配方、能力清单、安装脚本或自身工作流改变时自动触发，不因客户端代码改变而启动。不再安装 .NET SDK、编译或打包客户端；客户端归档、架构、安装包与启动检查由 Release 工作流负责。

这次同时处理后续日志中的阻塞：libvorbis 使用上游 CMake / Ninja 库构建，避开旧 Darwin Autotools 的 `-force_cpusubtype_ALL`；QuickClip 的中文参考测试显式选择中文，偏好设置入口按控件名称定位。未在本地运行媒体回归或 macOS 构建，也未测量新流程总耗时；实际节省时间以缓存命中的 Actions 步骤耗时为准。

```sh
git tag -a v1.0.5 -m "AvaMedia 1.0.5"
git push origin main
git push origin v1.0.5
```

版本统一传入 MSBuild、安装器、Info.plist 和文件名。源码默认版本位于 `Directory.Build.props`。

```powershell
./scripts/Publish.ps1 -Runtime win-x64 -Version 1.0.5
./scripts/Package-Windows.ps1 -Version 1.0.5
./scripts/Verify-WindowsInstaller.ps1 -Version 1.0.5
```

macOS 上使用 `scripts/Publish.ps1 -Runtime osx-arm64` 发布后运行 `scripts/Package-Mac.ps1`：PKG 安装至 `/Applications`，DMG 提供 Applications 快捷方式。后续 macOS 发布仅提供 ARM64，应用声明最低 macOS 13.4，与 ONNX Runtime 的 Mach-O 部署版本一致；归档检查会核对原生库与下载工具的最低版本。应用采用 ad-hoc 签名，没有 Developer ID 签名和公证；用户设备的音频设备仍需验收。

新构建将 FFmpeg / FFprobe 8.1.3、官方 yt-dlp 2026.08.19 和 QuickJS-NG 0.17.0 打包至 `tools`，固定来源并核对 SHA256，附版本清单和许可证。用户安装后无需执行媒体工具安装脚本。Windows 使用共享 DLL，引擎从 Linux 的 MinGW-w64 POSIX 工具链按固定源码构建；Mac 使用 ARM64 dylib，相对加载路径保留在应用内部，ZIP 保留工具执行权限。Windows 和 Mac 引擎包含 x264 / x265，均采用 GPL-3.0-or-later；对应源码归档上传同一 Release。详见 [macOS FFmpeg](FFMPEG-MACOS.md) 与 `scripts/windows/`。依赖和安装器许可保存在 `licenses/`，下载工具说明见 [视频下载](VIDEO-DOWNLOAD.md)。

参考：[GitHub 工作流语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)、[Inno 编译参数](https://jrsoftware.org/ishelp/topic_compilercmdline.htm)、[Inno Setup 6.4.3 许可](https://github.com/jrsoftware/issrc/blob/is-6_4_3/license.txt)。
