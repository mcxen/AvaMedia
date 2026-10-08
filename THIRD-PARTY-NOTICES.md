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
| PDFium native binaries | 157.0.8086 / chromium 8086 | PDFium BSD and dependency notices; bblanchon packaging Apache-2.0 |
| PdfPig | 0.1.13 | Apache-2.0, UglyToad and contributors |
| SkiaSharp | 2.88.9 | MIT; bundled Skia and dependencies have their own notices |
| HarfBuzzSharp | 8.3.1.1 | MIT bindings; bundled HarfBuzz has its own notices |
| ANGLE Windows native assets | 2.1.25547.20250602 | Package license and included third-party notices, including BSD terms |
| .NET 8 and ASP.NET Core runtime / Microsoft libraries | versions recorded in package manifest | MIT and accompanying third-party notices |
| Tmds.DBus.Protocol | 0.21.3 | MIT; only applicable on platforms that use it |
| Microsoft.ML.OnnxRuntime and managed bindings | 1.23.2 | MIT and bundled third-party notices; offline CPU inference |
| YuNet face detector | face_detection_yunet_2026may.onnx | MIT, Shiqi Yu and contributors; embedded unmodified model, attribution and SHA256 in `licenses/yunet/` |
| LaMa image inpainting model | inpainting_lama_2025jan.onnx | Apache-2.0, LaMa authors, Samsung Research and OpenCV contributors; downloaded separately, attribution and SHA256 in `licenses/lama/` |

For the complete package list, versions, authors, repository links and license expressions, see `licenses/dependencies.json` and `licenses/manifest.json`. Each package folder contains its original NuGet metadata and bundled license/notice files, plus the applicable SPDX text. Native library third-party notices from the packages must also be retained; the wrapper's MIT license does not replace them.

PDF page previews use [PDFium](https://pdfium.googlesource.com/pdfium/) through the pinned [bblanchon NuGet packages](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8086). macOS ARM64 and Windows x64 native assets are resolved by the application RID. Full binary and component notices are retained in `licenses/pdfium/`; package metadata and Apache-2.0 terms accompany the bindings' native dependencies. AvaMedia's small C API adapter is original code.

The page workspace's interaction design references [iLovePDF merge](https://www.ilovepdf.com/merge_pdf) and [split](https://www.ilovepdf.com/split_pdf). [Stirling-PDF scanner and compression tools](https://github.com/Stirling-Tools/Stirling-PDF) were reviewed as design references. Their UI assets and implementation code are not incorporated; page arrangement, image recompression and aged-paper rendering are original AvaMedia code.

## NSFW review vocabularies

The JoyTag/Danbooru review subset uses the Apache-2.0 vocabulary already retained
in `licenses/joytag/`. The 18 NudeNet class names are adapted from
[notAI-tech/NudeNet](https://github.com/notAI-tech/NudeNet/tree/6ccc81c6c305cccfd46d92b414f8a5c0a816574d),
AGPL-3.0; attribution and both upstream license files are retained in
`licenses/nsfw-review/`. Review groups and Chinese labels are AvaMedia additions.
NudeNet class candidates do not imply a bundled NudeNet detector or weights.

## External tools

FFmpeg and FFprobe 8.1.3 are bundled in the application `tools` directory as independent, replaceable processes. Both platform builds enable GPL and version3 for x264/x265 and are distributed under GPL-3.0-or-later; no nonfree component is enabled. Original component notices accompany the binaries in `licenses/media-tools/`. The original AvaMedia application remains AGPL-3.0-only. See [FFmpeg legal information](https://ffmpeg.org/legal.html). No FormatFactory FFmpeg binary is redistributed.

If you choose to distribute FFmpeg yourself, preserve its actual notices and license texts and provide the complete corresponding source for the precise binaries, build configuration, changes and external libraries, in accordance with their licenses. A link to a floating project homepage is not a substitute for corresponding source. A build with `--enable-nonfree` must not be included in the release.

The custom macOS ARM64 and Windows x64 runtimes are embedded in their application archives and installers. Matching source archives are published in the permanent `media-vVERSION` archive linked directly from each application Release, with exact FFmpeg and dependency sources, hashes/revisions, configuration and rebuild recipes. For example, application tag `v1.1.9` links to [media-v1.1.9](https://github.com/mcxen/AvaMedia/releases/tag/media-v1.1.9). These archives do not expire with Actions artifacts and are available without an additional charge. Windows adds zlib, bzip2, libiconv, NV codec headers, AMF headers and the Intel VPL dispatcher to the shared source set; their upstream licenses remain in the component notices. The Windows build applies the documented MinGW dispatcher guard change from its rebuild recipe. Compiler/system runtime components retain their applicable runtime library exceptions. See [the macOS build recipe](docs/FFMPEG-MACOS.md) and [the Windows source lock](scripts/windows/ffmpeg-sources.lock.json).

The macOS runtime also builds [zimg 3.0.6](https://github.com/sekrit-twc/zimg/tree/release-3.0.6) for FFmpeg's zscale HDR color conversion. zimg is licensed under WTFPL version 2; the unmodified upstream license is retained in `licenses/upstream/zimg-WTFPL.txt` and the runtime's component notices. Its exact source archive and SHA256 are included in the source lock and corresponding source asset.

New builds bundle the official yt-dlp 2026.08.19 and QuickJS-NG 0.17.0 executables as separate, replaceable processes in `tools/`. yt-dlp source uses the Unlicense; the executable includes independently licensed Python dependencies and EJS components. QuickJS-NG uses the MIT license; upstream release builds also include independently licensed mimalloc and, on Windows, MinGW-w64/GCC runtime components. Their notices and the GCC Runtime Library Exception are retained in `licenses/download-tools/`. Only the `qjs` runner is included. Versions, release URLs and SHA256 are recorded in `tools/download-tools.json`. See [yt-dlp third-party notices](https://github.com/yt-dlp/yt-dlp/blob/2026.08.19/THIRD_PARTY_LICENSES.txt) and [QuickJS-NG source](https://github.com/quickjs-ng/quickjs/tree/v0.17.0).

System fonts are read on the user's device when creating text PDFs; font files are not included in AvaMedia's distribution.

## Speech recognition and enhancement

[Whisper.net 1.9.1](https://github.com/sandrohanea/whisper.net/tree/98278acc38ae23590cdfa9859f78f089abae52a7) and its CPU native runtime wrap [whisper.cpp](https://github.com/ggml-org/whisper.cpp/tree/f24588a272ae8e23280d9c220536437164e6ed28), under MIT. Their licenses are retained in `licenses/speech/`. Publish builds keep only the requested platform's native libraries. Multilingual Whisper base-q5_1 and tiny-q5_1 weights are downloaded on demand from the maintainer's pinned [model repository](https://huggingface.co/ggerganov/whisper.cpp/tree/f281eb45af861ab5e5297d23694b7d46e090c02c), with sizes and SHA256 in `SpeechModelInstaller.Artifact`; they are managed by `ModelStore`.

Speech enhancement bundles the unmodified 297,646-byte `sh.rnnn` model from [GregorR/rnnoise-models](https://github.com/GregorR/rnnoise-models/tree/3eee541a283fd3b8f81b85b1748e3b9ccbefa04d/somnolent-hogwash-2018-09-01). The author states that the model data is not subject to copyright; the original statement is preserved in `licenses/speech/rnnoise-models-README.md`. Its SHA256 is `70bb6685eb0c2a1d18e2918dca3fbfbd39317010b1802eb1b6ea73a92f3fdec0`. Processing uses the bundled FFmpeg's `arnndn`, high-pass and loudness normalization filters. System fonts used to render video subtitles are selected on the user's device and are not redistributed.

On macOS, HEIC and supported photo previews use the device's [Apple ImageIO](https://developer.apple.com/documentation/imageio) and CoreFoundation system frameworks. These frameworks are not redistributed. HEIC encoding/conversion to JPEG, PNG or WebP uses the existing external FFmpeg build and its applicable licenses. [libheif](https://github.com/strukturag/libheif) and [SDWebImage](https://github.com/SDWebImage/SDWebImage) were reviewed as implementation references; neither their source nor binaries are incorporated. The new AvaMedia integration is original AGPL-3.0-only code.

## Platinum skin

The Mac OS 9 skin adapts the bevel color arrangement from [classic.css](https://github.com/npjg/classic.css), copyright (c) 2019 Nathanael Gentry, MIT. Its complete license is preserved in `licenses/upstream/classic-css-MIT.txt`. Window chrome and drawing code are implemented in C#; Apple bitmap assets and font files are not bundled. Control themes extend Avalonia SimpleTheme, covered by its MIT notices above.

Platinum embeds the unmodified **Fusion Pixel 12px Prop zh-Hans** font from [Fusion Pixel Font 2026.09.25](https://github.com/TakWolf/fusion-pixel-font/releases/tag/2026.09.25), under SIL Open Font License 1.1. It supplies portable pixel-style Latin and Simplified Chinese text. Attribution, the full OFL, upstream contributor notices and the font checksum are preserved in `licenses/fonts/fusion-pixel/`.

Windows 安装程序由 Inno Setup 6.4.3 编译，保留其安装器版权信息和来源标识；构建工具许可证见 licenses/installer/InnoSetup-6.4.3.txt，来源 https://github.com/jrsoftware/issrc/tree/is-6_4_3。
