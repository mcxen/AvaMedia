# 自动发布

日常 CI 仅在客户端源码、界面模板、项目或依赖配置改变时编译 `AvaMedia.Desktop` 及其 Core 依赖，不编译测试项目、不下载 FFmpeg、不运行媒体、截图或消融回归。只改文档、图片等资源时跳过。需要全量回归时，在 CI 的 **Run workflow** 中勾选 `full_verification`；该模式独立运行原有 `scripts/Verify.ps1`，不会先重复编译客户端。

Release 工作流接收 `vMAJOR.MINOR.PATCH` tag，依次执行：

1. Windows 媒体引擎与 macOS 并行准备；Windows 从固定源码交叉构建 x64 FFmpeg，macOS 复用或构建验证过的 ARM64 引擎。`Publish.ps1` 执行各平台所需的客户端 Release 编译，并把对应引擎及动态库复制进应用 `tools`。
2. 发布不包含 .NET 的 Windows x64 `portable.zip`；以固定版本 Inno Setup 6.4.3 生成每用户安装程序 `setup.exe`，实际验证安装、版本、原生启动、许可证和卸载。Windows 与 macOS 均使用独立原生启动器；缺少完整 .NET 8 时提供“安装运行时”按钮，完成后自动进入软件。
3. 在 Apple Silicon runner 构建包含 FFmpeg、FFprobe、动态库及 QuickJS 的 macOS ARM64 应用和 DMG，检查包内引擎加载并启动客户端。Windows 安装检查核对内置引擎文件哈希及版本。Windows 失败不会跳过 macOS 打包；两者均通过后才发布。不再生成 PKG。
4. Mac 应用 ZIP 只用于 CI 内部的 Mach-O 架构和 Unix 权限检查，不上传到主 Release。`Publish-Release.ps1` 先发布 `media-v版本` 媒体归档，再发布主 Release；主下载列表仅有 Mac DMG、Windows portable ZIP 和 setup EXE，SHA256 写入版本说明。

`Build-Bootstrap.ps1` 编译 Windows x64 C / macOS ARM64 Cocoa 启动器，不依赖 .NET 8；主程序采用 framework-dependent 发布。启动器通过官方 hostfxr API 按应用的 runtimeconfig 检查运行时版本、架构及 .NET / ASP.NET Core 两个框架，保持原有进程名、参数、播放器和更新入口。

`Prepare-Runtime.ps1` 在打包时从 Microsoft 官方 .NET 8 发布元数据中固定稳定版下载地址及 SHA512，保存为 `runtime-bootstrap.json`。首次安装只下载官方归档，不下载或执行远程脚本；Windows 使用系统 PowerShell 解压，macOS 使用系统 tar。运行时先在临时目录完成下载、校验与 hostfxr 检查，再原子移动到当前用户的 `AvaMedia/runtimes` 缓存。下载失败可重试；多个首次启动窗口共用安装锁。Windows 缓存位于 `%LOCALAPPDATA%\AvaMedia\runtimes`，Mac 位于 `~/Library/Application Support/AvaMedia/runtimes`。程序也检查环境变量和标准系统 .NET 安装目录，完整的现有运行时可离线启动；首次安装需要网络。

必要步骤失败时不发布 Release。构建 job 使用 `contents: read`，上传 job 单独获得 `contents: write` 和 GitHub 自带的 `GITHUB_TOKEN`；Actions 固定到核对过的 SHA。同一个 tag 可手动重跑，成品会重新上传。后续版本使用新 tag，不移动已发布 tag。

仅修复构建工作流时，可在默认分支手动运行 Release 并填写 `release_tag`，使用当前工作流重新构建已有标签的精确源码；版本、客户端源码和媒体源码始终取该标签，发布记录引用实际检出的提交。Windows 编译显式使用 UTF-8，确保原生启动窗口的中文字符串正确。

2026-10-07 核对 [v1.1.10–19](https://github.com/mcxen/AvaMedia/actions/runs/37575794689) 的失败记录，Windows 均停在 FFmpeg configure 的 `aom >= 2.0.0 not found using pkg-config`。依赖日志表明 `libaom.a` 和 `aom.pc` 已成功安装；配方未指定 pkg-config，FFmpeg 按交叉前缀选择未安装的 `x86_64-w64-mingw32-pkg-config`。配方现显式传入 `--pkg-config=pkg-config`，继续通过 `PKG_CONFIG_LIBDIR` 隔离 Windows 依赖，并将 `ffbuild/config.log` 复制到已有日志产物目录。v1.1.14–15 的 macOS Dispatcher 启动异常已由 `fdfb007` 修复，v1.1.16–19 的 macOS job 已通过。此修复只做 Python 语法检查；Windows 原生构建由后台工作流确认，旧 tag 不变，重跑旧 tag 仍使用旧配方。

[v1.1.22](https://github.com/mcxen/AvaMedia/actions/runs/37577379788) 和 [v1.1.23](https://github.com/mcxen/AvaMedia/actions/runs/37577797659) 已通过 FFmpeg 配置、编译和安装，停在 DLL 打包检查的 `Unbundled Windows dependency: AVICAP32.dll`。该库是 [Windows VFW 捕获组件](https://learn.microsoft.com/en-us/windows/win32/api/vfw/nf-vfw-capcreatecapturewindowa)，已补入系统 DLL 名单；其它非系统依赖仍必须随包提供。检查现保存所有二进制的 PE 导入日志，并一次报告全部未识别依赖。[默认分支构建](https://github.com/mcxen/AvaMedia/actions/runs/37577798056) 已成功保存源码和编译依赖缓存，FFmpeg 配置及打包修复不会使该依赖缓存失效。本次修复只做 Python 语法静态检查，安装包发布仍以新 tag 的后台 Release 结果为准。

[v1.1.24](https://github.com/mcxen/AvaMedia/actions/runs/37580118276) 的完整 DLL 日志进一步确认唯一未识别项为 `avformat-62.dll` 导入的 `ncrypt.dll`，现按 [Windows CNG 系统组件](https://learn.microsoft.com/en-us/windows/win32/api/ncrypt/nf-ncrypt-ncryptfreeobject) 处理。已基于该次全部 PE 导入记录静态核对系统库和包内 DLL；导入日志只保留架构及 DLL 名单，不再上传无关的完整 PE 展开数据。该次 Windows 媒体 job 命中源码和依赖缓存，用时约 5 分 37 秒。

`media-v版本` 指向同一应用提交，标记为 `latest=false`；其中保留完整对应的 FFmpeg / 依赖源码、重建配方、独立运行包与校验清单。主 Release 和许可证说明直接链接该归档，确保下载二进制的用户同时可获取精确源码。应用源码使用现有 Git tag 和 GitHub 自动生成的 Source code，不再上传重复的应用 source ZIP。GitHub 自带的两条 Source code 下载项由平台生成，不能通过删除 Release assets 隐藏。

2026-10-06 的 [v1.1.8 发布失败日志](https://github.com/mcxen/AvaMedia/actions/runs/37453172312) 有两个独立失败点：Windows `FunctionTests` 按已移除的“批量”菜单标题查找入口，现改用 `BatchCropMenuItem` 控件名称；macOS FriBidi 构建缺少 `help2man`，Release 和独立 FFmpeg 工作流现均安装该工具。旧 tag 保留原提交，重跑旧记录仍使用原工作流与源码；这些修复随后续版本 tag 生效。

后续原生 CI 的三个阻塞已修复：LAME 4.0 编码库关闭不需要的 mpg123 解码器；FFmpeg 显式链接 macOS 系统 libiconv；ICO 输出验证指定 PNG 所需的 RGBA 像素格式。[提交 18b61fc 的完整原生 CI](https://github.com/mcxen/AvaMedia/actions/runs/37471073518) 已通过，包含 225 项检查、27 个真实媒体输出、归档安装及上传，原生缓存保存成功。[Windows 客户端 CI](https://github.com/mcxen/AvaMedia/actions/runs/37459158475) 也已通过；这次仅修改 macOS 构建与验证脚本。该记录验证媒体工具工作流；客户端安装包以各版本的 Release 记录为准。

## macOS 文件打开方式

macOS 发布在签名前运行 `macos/Declare-MediaTypes.py`，从现有视频输入和播放器音频名单生成 `CFBundleDocumentTypes`、`UTImportedTypeDeclarations`。角色为 Viewer、优先级为 Alternate，不强制更改系统默认应用。只安装“天池万象转换.app”；在 Finder 中选择它打开视频或音频时，客户端通过 Avalonia 文件激活事件进入现有天池播放器。运行中的播放器接收后续文件和多文件播放列表。

如系统尚未刷新候选应用，可在选项页点击“注册播放打开方式”。设置某种文件的默认播放器仍由用户在 Finder“显示简介 → 打开方式 → 全部更改”中选择天池万象转换。

## 发布提速

2026-10-07 的 [v1.1.21 工作流](https://github.com/mcxen/AvaMedia/actions/runs/37576524975) 中，macOS job 用时约 2 分钟，Windows 媒体准备约 8 分半后在 FFmpeg configure 失败。Windows 原生 action 现分开缓存已校验的源码、编译完成的依赖与最终 FFmpeg 归档；源码在下载校验后保存，依赖在 FFmpeg configure 前保存。依赖 key 包含精确源码、依赖配方、编译环境及工具链版本，不包含 FFmpeg 的配置或诊断改动；即使 FFmpeg 后续失败，修复重跑也能复用已完成的依赖。缓存只精确匹配，安装前缀必须与记录一致，未完成的依赖构建不会写入成功缓存。

Release 和独立 macOS 工作流共用 `native-media` action，缓存验证通过的 FFmpeg 运行包、精确对应的源码包、校验清单及 QuickJS。缓存按 ARM64、Clang / SDK、源码锁文件、构建与验证脚本、能力清单和许可证内容精确匹配；普通客户端修改与版本号变更可以复用，相关输入变化则重建。不使用模糊匹配的原生运行包。缓存命中后仍核对两个 FFmpeg 归档的 SHA256、配方与源码锁哈希，重新执行原有原生媒体验证及 QuickJS 检查；客户端、安装器和发布检查保留。运行包与对应源码在原生验证成功后立即写入缓存，后续客户端失败不会丢失它们。默认分支的缓存可被 tag 工作流读取，首次构建或缓存淘汰时仍需完整编译。依据 [GitHub Cache 文档](https://github.com/actions/cache#cache-scopes)。

客户端构建 job 缓存 NuGet 下载包，key 根据项目依赖、中央包配置、targets、NuGet 配置与 SDK 配置生成，排除只含应用版本和编译选项的 `Directory.Build.props`。Windows 与 macOS 使用相同 key、相对缓存路径及 `enableCrossOsArchive`，复用默认分支 CI 保存的下载包；避免每个 tag 重复写入数百 MB 的平台缓存。正常 restore / build 仍解析当前依赖并补齐缺失的运行时包，不缓存客户端 `bin`、`obj` 或测试通过结果。依据 [跨平台缓存说明](https://github.com/actions/cache/blob/main/tips-and-workarounds.md#cross-os-cache)。

Windows 原生归档由 `windows-media` job 上传一次，客户端打包 job 只上传客户端成品；最终发布 job 直接下载原生 job 的归档，避免再次上传对应源码。日常 CI 与独立 macOS 工作流取消同一分支被新提交替代的旧运行；Release 按 tag 保留。ZIP、安装器及 FFmpeg 压缩包上传设置 `compression-level: 0`，避免二次压缩；验证日志单独保存，发布 job 只下载成品。依据 [Artifact 压缩说明](https://github.com/actions/upload-artifact#altering-compressions-level-speed-v-size)。

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

macOS 上使用 `scripts/Publish.ps1 -Runtime osx-arm64` 发布后运行 `scripts/Package-Mac.ps1`：仅生成 DMG，提供 Applications 快捷方式。后续 macOS 发布仅提供 ARM64，应用声明最低 macOS 13.4，与 ONNX Runtime 的 Mach-O 部署版本一致；内部归档检查会核对原生库与下载工具的最低版本。应用采用 ad-hoc 签名，没有 Developer ID 签名和公证；用户设备的音频设备仍需验收。

新构建将 FFmpeg / FFprobe 8.1.3、官方 yt-dlp 2026.08.19 和 QuickJS-NG 0.17.0 打包至 `tools`，固定来源并核对 SHA256，附版本清单和许可证。用户安装后无需执行媒体工具安装脚本。Windows 使用共享 DLL，引擎从 Linux 的 MinGW-w64 POSIX 工具链按固定源码构建；Mac 使用 ARM64 dylib，相对加载路径保留在应用内部，内部 ZIP 保留工具执行权限。Windows 和 Mac 引擎包含 x264 / x265，均采用 GPL-3.0-or-later；对应源码归档保存在主 Release 链接的媒体归档页。详见 [macOS FFmpeg](FFMPEG-MACOS.md) 与 `scripts/windows/`。依赖和安装器许可保存在 `licenses/`，下载工具说明见 [视频下载](VIDEO-DOWNLOAD.md)。

参考：[GitHub 工作流语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)、[Inno 编译参数](https://jrsoftware.org/ishelp/topic_compilercmdline.htm)、[Inno Setup 6.4.3 许可](https://github.com/jrsoftware/issrc/blob/is-6_4_3/license.txt)。

## 应用内更新

选项 → 版本更新支持启动检查、自动更新和静默更新；自动更新与静默更新默认关闭。启动检查和手动检查读取 `mcxen/AvaMedia` 的最新正式 Release，按系统架构及 Windows 安装形态选择安装包，下载后核对大小和 SHA256。SHA256 使用 Release asset digest，缺失时读取同一 Release 说明中的校验值；无校验值时仅提供发布页入口。

开启自动更新会后台下载；静默更新隐藏自动检查、下载和失败提示。手动检查始终显示结果，可点击“下载更新”。准备完成后仅在整个应用正常退出时安装，下一次启动使用新版；关闭窗口缩到托盘不会安装。关闭自动更新会取消后台下载及本次自动准备的更新，不取消用户手动准备的更新。

Windows 安装版使用 Inno Setup 静默安装到当前目录，便携版替换应用目录并保留相邻的 `.update-backup-标识` 原目录，避免丢失用户放在应用目录中的媒体。macOS ARM64 从 DMG 复制应用，检查版本和代码签名后替换当前 `.app`；替换失败尝试恢复原应用。应用目录必须可写，多实例未全部退出时不安装。安装失败记录在用户数据目录 `AvaMedia/Updates/install-error.txt`；非静默启动会显示失败，静默模式保留记录。更新不自动重启应用，也不会主动结束转换或播放。
