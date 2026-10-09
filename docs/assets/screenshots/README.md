# 界面截图 / Interface screenshots

更新日期 / Captured: **2026-10-09** · 开发构建 / Development build: **1.1.51**。

在 macOS 上编译并运行真实 Avalonia 客户端，打开四套皮肤的实际窗口，以 96 DPI 渲染窗口内容。中文与英文 README 共用原图。下面的 44 张图片包含各窗口初始状态与组件预览；每个链接可打开完整 PNG。

Captured from real Avalonia windows in a locally compiled macOS client, rendered at 96 DPI. Both READMEs share these Chinese-interface images. The 44 captures below show initial window states and component previews; each link opens the full PNG.

| 窗口 / Window | Light | Dark | Mac OS 9 · Platinum | Windows XP · Luna |
| --- | --- | --- | --- | --- |
| 主窗口 / Main window | [Light](main-light.png) | [Dark](main-dark.png) | [Platinum](main-macos9.png) | [Luna](main-winxp.png) |
| 文件路由 / File routing | [Light](route-light.png) | [Dark](route-dark.png) | [Platinum](route-macos9.png) | [Luna](route-winxp.png) |
| 偏好设置 / Preferences | [Light](settings-light.png) | [Dark](settings-dark.png) | [Platinum](settings-macos9.png) | [Luna](settings-winxp.png) |
| 输出设置 / Output settings | [Light](options-light.png) | [Dark](options-dark.png) | [Platinum](options-macos9.png) | [Luna](options-winxp.png) |
| 格式转换 / Conversion | [Light](convert-light.png) | [Dark](convert-dark.png) | [Platinum](convert-macos9.png) | [Luna](convert-winxp.png) |
| 视频下载 / Downloads | [Light](download-light.png) | [Dark](download-dark.png) | [Platinum](download-macos9.png) | [Luna](download-winxp.png) |
| 快速剪辑 / Quick Clip | [Light](editor-light.png) | [Dark](editor-dark.png) | [Platinum](editor-macos9.png) | [Luna](editor-winxp.png) |
| 剪辑导出 / Clip export | [Light](export-light.png) | [Dark](export-dark.png) | [Platinum](export-macos9.png) | [Luna](export-winxp.png) |
| 播放器 / Player | [Light](player-light.png) | [Dark](player-dark.png) | [Platinum](player-macos9.png) | [Luna](player-winxp.png) |
| 公共控件 / Common controls | [Light](components-light.png) | [Dark](components-dark.png) | [Platinum](components-macos9.png) | [Luna](components-winxp.png) |
| 媒体控件 / Media controls | [Light](media-components-light.png) | [Dark](media-components-dark.png) | [Platinum](media-components-macos9.png) | [Luna](media-components-winxp.png) |

[捕获清单 / Capture manifest](captures.json) 记录构建版本、平台、窗口尺寸、源码基线和图像 SHA256。[外观检查记录](../../APPEARANCE-REVIEW.md) 列出组件范围、实际交互与修正。

示例使用仓库的 `astronaut.png`，照片由 NASA 提供，属于公共领域；来源见 [素材说明](../../../tests/AvaMedia.BatchRotateTests/Fixtures/README.md)。图表与下载监控使用标明的示例数据。捕获工具使用独立状态目录，队列保持等待状态，不执行媒体导出、下载或模型推理。

The sample uses the repository's NASA public-domain `astronaut.png`; see the [fixture notes](../../../tests/AvaMedia.BatchRotateTests/Fixtures/README.md). Charts and download monitoring use labelled sample data. The capture tool uses isolated state, keeps jobs waiting, and does not run exports, downloads or model inference.

## 更新方式 / Updating

在仓库根目录准备 .NET 8 SDK、FFmpeg / FFprobe，然后执行：

From the repository root, with the .NET 8 SDK and FFmpeg / FFprobe available:

```sh
mkdir -p artifacts/ui-capture/samples
cp tests/AvaMedia.BatchRotateTests/Fixtures/astronaut.png artifacts/ui-capture/samples/航天员.png
ffmpeg -hide_banner -loglevel error -loop 1 \
  -i artifacts/ui-capture/samples/航天员.png \
  -vf "scale=720:720,pad=1280:720:280:0:color=0x101820" \
  -t 4 -r 25 -c:v libx264 -pix_fmt yuv420p -y artifacts/ui-capture/samples/航天员.mp4
ffmpeg -hide_banner -loglevel error -f lavfi -i sine=frequency=440:duration=4 \
  -y artifacts/ui-capture/samples/示例音轨.wav
printf '截图示例文档。\n用于展示文件路由。\n' > artifacts/ui-capture/samples/使用说明.txt

dotnet build tools/AvaMedia.UiCapture/AvaMedia.UiCapture.csproj -c Release
dotnet tools/AvaMedia.UiCapture/bin/Release/net8.0/AvaMedia.UiCapture.dll \
  artifacts/ui-capture/captures artifacts/ui-capture/samples --hold
```

`--hold` 保留最后一套皮肤的组件窗口，可现场切换外观、打开路由和设置；关闭组件窗口退出。省略它则在生成 44 张图后退出。截图仅在用户明确要求时更新，遵循 [`AGENTS.md`](../../../AGENTS.md) 的验证范围。

`--hold` keeps the final component window open for skin switching, routing and preferences. Close that window to exit. Omit the flag to exit after all 44 captures. Update screenshots only when requested, within the validation scope in [`AGENTS.md`](../../../AGENTS.md).

检查原图后将 `captures/*.png` 复制到本目录，重新记录清单中的源码基线和图像 SHA256，更新两份 README 的日期。将 `player-dark.png` 与 `player-macos9.png` 同步到上一级 `docs/assets/`，供现有播放器文档与网站使用。素材、状态和临时审阅图留在 Git 忽略的 `artifacts/` 内。

Inspect the original PNGs before copying them here, refresh the manifest's source baseline and image hashes, and update both READMEs' dates. Also copy the two player images to `docs/assets/` for existing player documentation and the website. Keep fixtures, state and temporary review sheets in the Git-ignored `artifacts/` directory.
