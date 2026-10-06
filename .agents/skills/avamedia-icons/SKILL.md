---
name: avamedia-icons
description: 为 AvaMedia 生成、补充或替换 v2 系列图标、Mac OS 9 专属功能图标和应用图标，完成透明 PNG、应用图标封装、皮肤切换接入与检查。
---

# AvaMedia 图标生产

沿用本项目已确认的 v2 图标系列。功能图标是独立透明 PNG；应用图标同时用于可执行文件、窗口和应用包。以本 Skill 所在仓库为项目根目录，不依赖某台电脑的绝对路径。

用户指定 Mac OS 9 / Platinum 时，使用独立的 `macos9` 系列，先读 [references/macos9.md](references/macos9.md)。不要将该系列改成 v2 的光滑轮廓。未指定皮肤时默认 v2；应用标识继续沿用 v2，除非用户另有要求。

## 参考与提示词

先读 [references/style.md](references/style.md)，查看以下实际素材：

- `docs/assets/feature-icons-v2-preview.png`：已确认的整套风格与功能符号。
- `src/AvaMedia.Desktop/Assets/FeatureIcons/v2/rotate.png`：本批次的风格锚点。
- 功能图标的原始提示词：`src/AvaMedia.Desktop/Assets/FeatureIcons/v2/manifest.json`。
- 应用图标的原始提示词：`src/AvaMedia.Desktop/Assets/AppIcon/v2/manifest.json`。

补充同类图标时，复用最接近的现有提示词，仅修改主体与必要布局；保留配色、视角、轮廓、高光及透明要求。应用标识使用蓝色胶片、橙色播放键和绿色转换箭头，少量粗线条图形需在 16–48 像素仍可辨认。

## 生成与保存

使用内置 `image_gen.imagegen`，每个独立图标调用一次，`transparent_background: true`。通过 `referenced_image_paths` 提供实际风格参考；先查看未见过的本地图像。没有内置工具时说明限制，不擅自切换需要密钥的 CLI/API。

生成 PNG 中不放格式名称。MP4、MP3、PNG、PDF 等标签由 `FeatureIcon` 绘制。应用标识不放文字。不要生成图标拼图后切割成素材。

将选定原图从工具返回的位置复制进项目，保留工具原件。新功能素材存入 `src/AvaMedia.Desktop/Assets/FeatureIcons/v2/`；应用素材存入 `src/AvaMedia.Desktop/Assets/AppIcon/v2/`。用户要求替换或全面覆盖时更新相应消费者；普通新增或试稿使用新文件名，保留现有素材。

Mac OS 9 功能素材存入 `src/AvaMedia.Desktop/Assets/FeatureIcons/macos9/`，使用同名 `Kind`，与 v2 分开保存；完整提示词与哈希在该目录的 `manifest.json`。每一枚单独生成，以该目录的 `rotate.png` 为风格锚点，其他功能的 v2 图标仅作语义参考。

更新相应 manifest：实际使用的完整提示词、生成工具与模式、参考图的相对路径、PNG 尺寸和 SHA-256。图像修订继续用内置图像工具；格式封装只编码和缩放已选原图，不重画主体或修改配色。

## 接入项目

- 功能图标由 `Controls/FeatureIconAssets.cs` 缓存加载，`Controls/FeatureIcon.cs` 绘制。添加新 `Kind` 时更新允许集合及相关功能入口，保持原有格式标签行为。
- `FeatureIcon` 依据自身的 `ActualThemeVariant` 选择 Mac OS 9 素材，并在皮肤变化时刷新；缓存按系列和 Kind 分开。检查已打开窗口、随后打开的子窗口，以及切回 Light / Dark 的行为。
- `AvaMedia.Desktop.csproj` 中的 `AvaloniaResource` 必须包含最终素材。应用 ICO 用于 `ApplicationIcon`、主窗口和没有对应功能的窗口；应用 PNG 由 `Controls/ApplicationArtwork.cs` 提供。
- 功能窗口通过 `Controls/WindowArtwork.cs` 使用对应的功能 PNG，并监听 `ActualThemeVariantChanged` 同步切换原生窗口图标；`PlatinumWindowFrame` 的自绘标题图标绑定同一个窗口的 `WindowArtwork.Image`。通用转换窗口按 `Feature.Icon` 设置 Kind，独立窗口在 `WindowArtwork` 中维护默认对应关系。新增功能窗口必须接入这一映射，不要再次将应用图标写死到功能标题栏。
- 用户要求覆盖实际使用时，读取 `Start-AvaMedia.cmd` 的当前优先启动路径，并更新它指向的构建，不依赖固定的旧输出目录。可执行 `pwsh -File scripts/Publish.ps1 -Runtime win-x64 -OutputDirectory <实际输出目录>`。旧程序锁住输出文件时，发布到新的独立目录，再调整启动脚本优先使用新版；不要为图标更新擅自关闭用户正在使用的窗口。
- 应用图标需要重新封装时，在 Windows 上执行：

  ```powershell
  pwsh -File .agents/skills/avamedia-icons/scripts/Export-AppIcon.ps1 -Source src/AvaMedia.Desktop/Assets/AppIcon/v2/app.png -OutputDirectory src/AvaMedia.Desktop/Assets/AppIcon/v2
  ```

  脚本输出带 16–256 像素档位的 ICO、16–1024 像素 PNG 档位及 ICNS。原始 PNG 保持不变。macOS 应用包从 `scripts/Publish.ps1` 将 ICNS 复制到 `Contents/Resources/AvaMedia.icns`，`scripts/macos/Info.plist` 使用 `CFBundleIconFile` 引用它。macOS 上可按 [Apple 的 iconutil 流程](https://developer.apple.com/library/archive/documentation/GraphicsAnimation/Conceptual/HighResolutionOSX/Optimizing/Optimizing.html) 从导出的 `AvaMedia.iconset` 重新封装。

## 验收与交付

检查透明边角，并在浅色、深色背景查看 48/64 像素功能图标；应用标识再检查 16/24/32 像素。用项目实际控件渲染预览，检查符号、箭头方向、标签清晰度和留白。预览不是另一份生成原图。

在 Windows 上运行 [scripts/Check-IconAssets.ps1](scripts/Check-IconAssets.ps1) 检查素材完整性、manifest 哈希、目录与应用图标封装。渲染脚本会验证实际功能目录的图标覆盖和全局窗口图标加载：

```powershell
dotnet run --project .agents/skills/avamedia-icons/scripts/IconPreview -c Release -- . artifacts/icon-preview
```

它输出 v2 和 Mac OS 9 系列的 48/64/120 像素预览，应用标识各尺寸预览及 `coverage.json`。Mac OS 9 预览通过真正的皮肤继承加载素材。项目有本地 SDK 时，可将 `dotnet` 替换为 `.tools/dotnet/dotnet.exe`。接入后构建桌面项目；运行预览或 UI / Skin 检查前遵守项目 `AGENTS.md` 的检查范围，用户未明确要求时不自行运行。不要修改其他功能实现。

交付中说明生成模式、保存位置、提示词 manifest 和已验证的使用位置。应用包在非 macOS 主机检查时，只报告打包与结构检查结果，不声称已验证 Finder/Dock 的原生显示。
