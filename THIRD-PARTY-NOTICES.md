# Third-party notices

Copyright (c) 2026 AvaMedia contributors. The original AvaMedia application code and vector artwork are licensed under AGPL-3.0-only; see `LICENSE` and `COPYRIGHT`. Third-party components retain the licenses stated below. The original FormatFactory application, name and artwork remain the property of their respective rights holders. AvaMedia does not redistribute its proprietary DLLs or artwork.

The distribution includes the following independently licensed components. Preserve this file and the entire `licenses` directory when redistributing it.

| Component | Version | License / provenance |
|---|---|---|
| Avalonia and SimpleTheme | 11.3.22 | MIT, AvaloniaUI OÜ |
| MicroCom.Runtime | 0.11.0 | MIT |
| NAudio and associated modules | 2.2.1 | MIT, Mark Heath and contributors |
| QRCoder | 1.8.0 | MIT, Raffael Herrmann and contributors; local QR code generation |
| PDFsharp | 6.2.4 | MIT, empira Software GmbH |
| PdfPig | 0.1.13 | Apache-2.0, UglyToad and contributors |
| SkiaSharp | 2.88.9 | MIT; bundled Skia and dependencies have their own notices |
| HarfBuzzSharp | 8.3.1.1 | MIT bindings; bundled HarfBuzz has its own notices |
| ANGLE Windows native assets | 2.1.25547.20250602 | Package license and included third-party notices, including BSD terms |
| .NET 8 and ASP.NET Core runtime / Microsoft libraries | versions recorded in package manifest | MIT and accompanying third-party notices |
| Tmds.DBus.Protocol | 0.21.3 | MIT; only applicable on platforms that use it |
| Microsoft.ML.OnnxRuntime and managed bindings | 1.23.2 | MIT and bundled third-party notices; offline CPU inference |
| YuNet face detector | face_detection_yunet_2026may.onnx | MIT, Shiqi Yu and contributors; embedded unmodified model, attribution and SHA256 in `licenses/yunet/` |

For the complete package list, versions, authors, repository links and license expressions, see `licenses/dependencies.json` and `licenses/manifest.json`. Each package folder contains its original NuGet metadata and bundled license/notice files, plus the applicable SPDX text. Native library third-party notices from the packages must also be retained; the wrapper's MIT license does not replace them.

## External tools

FFmpeg and FFprobe are invoked as external, replaceable processes. The AvaMedia release ZIP does **not** contain their binaries. FFmpeg is licensed under LGPL 2.1-or-later with version-dependent optional GPL components; our development build enables version3 and is therefore subject to the actual LGPLv3-or-later terms reported by that build. See [FFmpeg legal information](https://ffmpeg.org/legal.html). No FormatFactory FFmpeg binary is redistributed.

If you choose to distribute FFmpeg yourself, preserve its actual notices and license texts and provide the complete corresponding source for the precise binaries, build configuration, changes and external libraries, in accordance with their licenses. A link to a floating project homepage is not a substitute for corresponding source. A build with `--enable-nonfree` must not be included in the release.

The custom macOS ARM64 FFmpeg runtime is a separate release asset, not embedded in the AvaMedia application archive. It enables GPL and version3 for x264/x265 and is distributed under GPL-3.0-or-later. The matching source asset includes the exact FFmpeg and dependency sources, source hashes/revisions, configuration and rebuild scripts; the runtime preserves component notices. No nonfree component or proprietary FormatFactory binary is included. See [the macOS build recipe](docs/FFMPEG-MACOS.md).

The macOS runtime also builds [zimg 3.0.6](https://github.com/sekrit-twc/zimg/tree/release-3.0.6) for FFmpeg's zscale HDR color conversion. zimg is licensed under WTFPL version 2; the unmodified upstream license is retained in `licenses/upstream/zimg-WTFPL.txt` and the runtime's component notices. Its exact source archive and SHA256 are included in the source lock and corresponding source asset.

New builds bundle the official yt-dlp 2026.08.19 and QuickJS-NG 0.17.0 executables as separate, replaceable processes in `tools/`. yt-dlp source uses the Unlicense; the executable includes independently licensed Python dependencies and EJS components. QuickJS-NG uses the MIT license; upstream release builds also include independently licensed mimalloc and, on Windows, MinGW-w64/GCC runtime components. Their notices and the GCC Runtime Library Exception are retained in `licenses/download-tools/`. Only the `qjs` runner is included. Versions, release URLs and SHA256 are recorded in `tools/download-tools.json`. See [yt-dlp third-party notices](https://github.com/yt-dlp/yt-dlp/blob/2026.08.19/THIRD_PARTY_LICENSES.txt) and [QuickJS-NG source](https://github.com/quickjs-ng/quickjs/tree/v0.17.0).

System fonts are read on the user's device when creating text PDFs; font files are not included in AvaMedia's distribution.

## Platinum skin

The Mac OS 9 skin adapts the bevel color arrangement from [classic.css](https://github.com/npjg/classic.css), copyright (c) 2019 Nathanael Gentry, MIT. Its complete license is preserved in `licenses/upstream/classic-css-MIT.txt`. Window chrome and drawing code are implemented in C#; Apple bitmap assets and font files are not bundled. Control themes extend Avalonia SimpleTheme, covered by its MIT notices above.

Platinum embeds the unmodified **Fusion Pixel 12px Prop zh-Hans** font from [Fusion Pixel Font 2026.09.25](https://github.com/TakWolf/fusion-pixel-font/releases/tag/2026.09.25), under SIL Open Font License 1.1. It supplies portable pixel-style Latin and Simplified Chinese text. Attribution, the full OFL, upstream contributor notices and the font checksum are preserved in `licenses/fonts/fusion-pixel/`.

Windows 安装程序由 Inno Setup 6.4.3 编译，保留其安装器版权信息和来源标识；构建工具许可证见 licenses/installer/InnoSetup-6.4.3.txt，来源 https://github.com/jrsoftware/issrc/tree/is-6_4_3。
