# macOS ARM64 定制 FFmpeg

构建配方锁定 FFmpeg 8.1.3 和 15 个依赖的源码版本；压缩包校验 SHA256，Git 源码核对完整提交号。配置见 [源码锁文件](../scripts/macos/ffmpeg-sources.lock.json)。仅生成 ARM64 运行包，所有依赖从源码构建，不链接 Homebrew 的运行库。

FreeType 从官方 SourceForge 发行镜像下载，Savannah 作为备用；两者使用同一固定 SHA256。下载设置连接和总耗时上限，连接失败后尝试备用地址；校验不一致则立即失败。来源依据 [FreeType 下载说明](https://freetype.org/download.html)。

FFmpeg 自身采用共享库；所需第三方库静态编入对应媒体库。运行包只包含 `ffmpeg`、`ffprobe`、FFmpeg dylib、许可证、构建信息与验证报告。按 dylib 的实际 install name 保存一份文件，避免把版本别名重复复制。排除 ffplay、头文件、静态归档、开发工具和文档。

保留客户端已有的容器、解码器和内置滤镜，不使用 `--disable-everything`。外部组件按实际调用配置：

| 能力 | 实现 |
| --- | --- |
| H.264 / HEVC 软件编码 | x264、x265 |
| 苹果 HLG / PQ / Dolby Vision 基础层转 SDR | zimg 3.0.6 提供 zscale，配合 tonemap、sidedata；启用 `--enable-libzimg` |
| AV1、AVIF、VP8 / VP9、WebP | libaom、libvpx、libwebp |
| MP3、Opus、Vorbis | LAME、Opus、libogg / libvorbis |
| 字幕烧录、中文与文字叠加 | libass、FreeType、HarfBuzz、FriBidi、libunibreak；字幕字体来自 CoreText，多宫格时间戳直接读取系统字体 |
| 硬件编解码与音频 | VideoToolbox、AudioToolbox |
| HTTPS | 系统 Secure Transport |

未引入客户端没有使用的 VMAF、libplacebo、SDL、ICU、Cairo、GLib 或额外 AV1 编码器。HarfBuzz 的字体子集、GPU 和工具组件关闭；保留文字整形。保留 CPU 检测和 ARM 优化指令，避免只适用于构建机器。`--enable-small` / `-Os` 偏向体积；实际耗时记录在客户端消融报告中。

LAME 4.0 仅构建 MP3 编码库，显式关闭命令行前端和其默认的外部 mpg123 解码器（`--disable-frontend --disable-decoder`）；MP3 解码由 FFmpeg 内置组件承担，无需新增 mpg123 依赖。

zimg 固定上游 `release-3.0.6` 源码归档及 SHA256。静态构建使用 macOS 的 libc++（`STL_LIBS=-lc++`），关闭示例及测试程序。WTFPL v2 许可证原文随组件声明保留，精确源归档纳入对应源码包。客户端压缩的 HDR → SDR 行为见 [视频压缩](VIDEO-COMPRESSION.md)。

## 构建与安装

在 Apple Silicon macOS 上安装 Xcode 命令行工具和 Python 3.12+，然后执行：

```sh
brew install cmake meson ninja pkgconf autoconf automake libtool help2man
python3 scripts/macos/Build-FFmpeg.py
```

`help2man` 用于 FriBidi 的 Autotools 构建生成手册页，仅作为构建工具；两个 macOS 工作流均安装它，运行包不附带。上游规则见 [FriBidi 1.0.17 的 Makefile](https://github.com/fribidi/fribidi/blob/v1.0.17/bin/Makefile.am)。

libvorbis 使用其 [上游 CMake 配方](https://github.com/xiph/vorbis/blob/v1.3.7/CMakeLists.txt) 与 Ninja 构建静态库，显式链接本配方生成的 libogg，不执行旧 Autotools Darwin 链接参数或额外测试程序。CI / Release 通过共同 action 缓存已验证运行包及对应源码，命中后仍执行完整原生验证；本地可使用 `python3 scripts/macos/Build-FFmpeg.py --reuse-built` 复用当前目录中匹配的归档。缓存规则见 [发布提速](RELEASE.md#发布提速)。

产物位于 `artifacts/`：

- `AvaMedia-FFmpeg-8.1.3-osx-arm64.tar.gz`：独立媒体运行包。
- `AvaMedia-FFmpeg-8.1.3-source.tar.gz`：精确对应的源码、依赖、锁文件和重建配方。
- `AvaMedia-FFmpeg-8.1.3-SHA256SUMS.txt`：两个归档的校验清单。

本地运行包安装：

```sh
bash scripts/Install-MediaTools-macOS.sh \
  --archive artifacts/AvaMedia-FFmpeg-8.1.3-osx-arm64.tar.gz \
  --checksums artifacts/AvaMedia-FFmpeg-8.1.3-SHA256SUMS.txt
```

不带参数时，从最新 AvaMedia Release 下载定制运行包并核对清单。安装至 `~/Library/Application Support/AvaMedia/tools/ffmpeg-runtime/<归档SHA256>`，程序入口通过相对链接切换；下载与解压的临时目录退出时清理。FFmpeg 安装不需要 Homebrew 或 Python；加 `--with-yt-dlp` 时另用 Homebrew 安装可选下载工具。已有的外部路径配置仍可替换媒体引擎。

## 验证与发布

[原生构建工作流](../.github/workflows/ffmpeg-macos.yml) 在相关源码变更、Pull Request 或手动触发时运行。tag 的 [Release 工作流](../.github/workflows/release.yml) 同样先构建与验证，再上传运行包、对应源码和清单。

Release 的 macOS job 独立读取 tag 版本，与 Windows 并行执行，避免 Windows 测试失败后完全跳过 Mac 验证。最终 Release 仍要求两个平台都通过。

原生检查覆盖单一 ARM64 架构、动态库相对路径、签名、文件哈希、编码器 / 滤镜 / 容器 / 协议；不包含 AVFoundation 采集设备。实际编码与解码 27 个输出，比较字幕和时间戳的真实像素。Release 另运行客户端音频、字幕、批量工具和消融测试。独立工作流还把归档安装到带空格的新目录，验证运行库与原构建目录无关。

独立工作流同时从固定源码构建 QuickJS，并按 `Directory.Build.props` 的开发版本原生发布应用 ZIP、检查完整归档；产物分别保存在 `quickjs-osx-arm64`、`ffmpeg-osx-arm64` 与 `macos-application-archive`。它不会创建 GitHub Release。

本地已完成 15 项源码校验、工作流与脚本语法检查、客户端构建，以及批量工具 28 项、音频选项 32 项、字幕 32 项和消融 35 项回归。当前开发环境为 Windows，尚未完成定制运行包的 macOS 原生构建；实际体积、原生输出和耗时以首次 ARM64 构建的 `build.json`、`verification.json` 与日志为准。硬件设备能力按实际设备验收。

## 许可证

此定制运行包包含 x264 / x265，使用 `--enable-gpl --enable-version3`，按 **GPL-3.0-or-later** 交付；未启用 nonfree。Windows 的独立 LGPL 工具方案不变。AvaMedia 应用及构建脚本仍采用 AGPL-3.0-only，运行包源码归档同时保留各依赖的原始源码、版权声明和重建所需脚本。系统字体不随包再分发。

配置与许可依据：[FFmpeg 构建脚本](https://github.com/FFmpeg/FFmpeg/blob/n8.1.3/configure)、[FFmpeg 许可说明](https://ffmpeg.org/legal.html)、[libass 字体支持](https://github.com/libass/libass)、[HarfBuzz 配置](https://github.com/harfbuzz/harfbuzz/blob/14.5.1/meson.options)。
