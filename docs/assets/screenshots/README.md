# README 截图 / README screenshots

更新日期 / Captured: **2026-10-07**。

截图来自 macOS 上已有的本地 Release 开发构建，使用客户端 `--capture` 入口渲染实际窗口内容。中文与英文 README 共用原图，图中的界面语言为简体中文；配图不表示全部功能已通过回归验收。

Captured from an existing local Release development build on macOS using the client's `--capture` window-rendering entry point. Both READMEs share the original PNGs, with the app set to Simplified Chinese. These documentation images do not establish functional regression coverage.

| 文件 / File | 内容 / Content | 捕获输出 / Capture output |
| --- | --- | --- |
| `main-light.png` | 浅色主窗口 / Light main window | `light/main.png` |
| `main-macos9.png` | Platinum 主窗口 / Platinum main window | `macos9/main.png` |
| `editor-dark.png` | 深色快速剪辑 / Dark Quick Clip | `dark/quick-clip.png` |
| `export-light.png` | 浅色导出设置 / Light export settings | `light/quick-clip-export.png` |
| `download-light.png` | 浅色视频下载 / Light video downloader | `light/download.png` |

编辑器示例使用仓库已有的 `astronaut.png`，原始照片由 NASA 提供，属于公共领域。来源和许可说明见 [素材说明](../../../tests/AvaMedia.BatchRotateTests/Fixtures/README.md)。演示视频只用于文档配图，不加入队列执行媒体导出。

The editor sample uses the existing `astronaut.png` fixture, a NASA public-domain photograph. See the [fixture notes](../../../tests/AvaMedia.BatchRotateTests/Fixtures/README.md) for attribution. The demo video is used only to populate documentation screenshots; no export job is queued.

## 更新方式 / Updating

在仓库根目录执行，使用已编译的客户端与可用的 FFmpeg / FFprobe。下列命令只生成截图素材与实际窗口截图，不运行构建、功能回归、截图断言或性能测量。媒体工具不在 PATH 时，可设置 `AVAMEDIA_FFMPEG` / `AVAMEDIA_FFPROBE`。

Run from the repository root with an already compiled client and available FFmpeg / FFprobe. These commands create the demo asset and window captures; they do not build the project or run regression tests, screenshot assertions or benchmarks. Set `AVAMEDIA_FFMPEG` / `AVAMEDIA_FFPROBE` if needed.

```sh
mkdir -p artifacts/readme
ffmpeg -hide_banner -loglevel error -loop 1 \
  -i tests/AvaMedia.BatchRotateTests/Fixtures/astronaut.png \
  -t 4 -r 25 -c:v libx264 -pix_fmt yuv420p -y artifacts/readme/demo.mp4

dotnet src/AvaMedia.Desktop/bin/Release/net8.0/AvaMedia.Desktop.dll \
  --capture artifacts/readme/light --quick-clip artifacts/readme/demo.mp4 --download
dotnet src/AvaMedia.Desktop/bin/Release/net8.0/AvaMedia.Desktop.dll \
  --capture artifacts/readme/dark --dark --quick-clip artifacts/readme/demo.mp4
dotnet src/AvaMedia.Desktop/bin/Release/net8.0/AvaMedia.Desktop.dll \
  --capture artifacts/readme/macos9 --macos9
```

按上表将需要的 PNG 复制到本目录，保持文件名不变，并同步更新两份 README 中的截图日期与说明。演示视频和捕获状态留在 Git 忽略的 `artifacts/` 中。

Copy the PNGs listed above into this directory, keeping their names, and update the capture date and captions in both READMEs. Keep the demo video and capture state in the Git-ignored `artifacts/` directory.
