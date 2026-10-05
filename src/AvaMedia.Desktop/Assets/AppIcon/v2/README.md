# AvaMedia 应用图标

应用标识由 Codex 内置 `image_gen.imagegen` 参考已确认的功能图标系列生成，组合蓝色胶片、橙色播放键与绿色转换箭头。透明 PNG 原图为 `app.png`（1254 × 1254），完整生成提示词与 SHA-256 位于 [manifest.json](manifest.json)。

- `app.ico`：Windows 应用及窗口图标，含 16、20、24、32、40、48、64、128、256 像素帧。
- `app.icns`：macOS 应用包图标，含标准及 Retina PNG 档位；发布时复制为 `Contents/Resources/AvaMedia.icns`。
- `AvaMedia.iconset/`：符合 Apple 命名规则的图标集，可在 macOS 用 `iconutil -c icns` 重新封装。
- `app-*.png`：各尺寸封装用 PNG。只缩放和编码已选原图，保留透明通道。

应用标识由桌面项目的 `ApplicationIcon`、`App.axaml` 全局窗口样式和 Mac OS 9 自绘标题栏共同使用。功能目录由现有 20 款功能图标覆盖，格式文字继续由界面绘制。

项目内图标生产入口：[avamedia-icons/SKILL.md](../../../../../.agents/skills/avamedia-icons/SKILL.md)。该 Skill 包含本批次风格、参考图、提示词入口、封装脚本与检查脚本。

macOS 图标集与封装方式参考 [Apple 图标集说明](https://developer.apple.com/library/archive/documentation/Xcode/Reference/xcode_ref-Asset_Catalog_Format/IconSetType.html)；在 Windows 上已检查 ICNS 的文件结构与各 PNG 档位，Finder/Dock 的原生显示需在 macOS 验证。
