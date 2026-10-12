# AI progress capture

Captures the existing Chinese desktop windows using deterministic in-memory jobs,
subtitle cues, activity histories, six speech segments (segment three running),
two waiting subtitle jobs, and a classification snapshot with 412 of 1284 files
processed. Classification uses generated people in a furnished room with five
garment colours, a six-frame video (frame four running), partial clothing colour
evidence, scene scores and group additions. Subtitle covers and a synthetic audio envelope are fixtures;
the tool does not submit tasks or run AI inference. Settings and fixture files
and thumbnail caches are isolated in existing temporary XDG data/config/cache
directories and removed on exit. The capture also runs from that temporary
directory so relative application paths cannot create cache folders in the repo.

Native capture uses `$DISPLAY` (default `:11`) and X11 scale 1. The harness lays out
each complete window at the `.fw` dimensions in the v2 HTML demos, then renders
its client area using `RenderTargetBitmap` at 96 dpi with one output pixel per
logical pixel. Window minimum sizes are overridden only in the capture process.
No cropping or image resizing is applied to the implementation captures.

If the environment blocks X11 sockets, pass `--headless`
to render the same controls with Avalonia Headless and Skia. This mode is recorded
in `captures.json`; it does not claim native X11 window verification. In a sandbox
where the review directory is read-only, use `/tmp/avamedia-ai-progress-v2/impl`
and pass `--demos /workspace/AvaMedia-sidebar-review/ai-progress/v2` to the Python script.

```sh
~/.dotnet/dotnet build tools/AvaMedia.AiProgressCapture -p:EnableWindowsTargeting=true --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
~/.dotnet/dotnet tools/AvaMedia.AiProgressCapture/bin/Debug/net8.0/AvaMedia.AiProgressCapture.dll /workspace/AvaMedia-sidebar-review/ai-progress/v2/impl
python3 tools/AvaMedia.AiProgressCapture/sidebyside.py /workspace/AvaMedia-sidebar-review/ai-progress/v2/impl
```

For the classification window alone, add `--classification` to the capture command
and `--name progress-from-tasklist-desktop` to the comparison command.

The output-directory argument is optional and defaults to `/tmp/avamedia-ai-progress-v2/impl`.
The capture outputs are `subtitle-submit-desktop.png` (940 × 560),
`subtitle-progress-desktop.png` (1060 × 712),
`progress-from-tasklist-desktop.png` (1040 × 690), and `captures.json`.

`sidebyside.py` requires Pillow (`python3 -m pip install --user pillow` if missing).
It checks all six demo names, skips those without an implementation PNG, and
writes `compare-<name>.png` with demo on the left and implementation on the right.
The implementation remains at native size. Desktop demos are cropped to the
complete `.fw` target window and converted from their 2× PNGs to logical pixels,
so card heights can be compared at 1:1 scale without the background windows.
Web and mobile demos retain their complete viewport. Demo PNGs default to the output directory's
parent, or can be specified with `--demos`.

Native X11 capture and visual review completed on 2026-10-12. The subtitle
fixture reports 186 cues, 12:22 of 26:40 recognized, segment 3/6, and two waiting
jobs. Demo screenshots include background windows and illustrative media; the
harness captures only the real window client and uses synthetic media fixtures.

## 通用组件预览与 demo 并排对照

`--gallery-view` 打开可操作的 12 类组件展示页，右上角切换浅色/深色。`--gallery` 用真正的 AvaMedia.Controls 控件截取浅色与深色共 24 个区块，不使用 HTML 截图充当实现。

```sh
DISPLAY=:11 AVAMEDIA_CHROME=mac XDG_DATA_HOME=/tmp/avademo/data \
  ~/.dotnet/dotnet run --project tools/AvaMedia.AiProgressCapture -- \
  /workspace/AvaMedia-sidebar-review/components/impl --gallery
python3 tools/AvaMedia.AiProgressCapture/compare_components.py
```

输入规格为评审目录中的 `components.html` / `components-dark.html`。`export_components.py` 仅提取控件位置、示例内容、字体标注与文字语义资源键到 `component-layout.json`，所有控制模板、状态、表面均由 Avalonia 渲染。比较脚本将 2x demo 缩到相同 1x 逻辑尺寸，生成 `compare-s01..s12-light/dark.png` 和 `gallery-light/dark.png`，不裁切组件。

分类窗口用 `--classification` 单独截图，再调用既有 `sidebyside.py`；交付副本命名为 `impl-progress.png`、`compare-progress.png`。调用方提供的 XDG 目录保留使用，未提供的目录在临时环境中创建。
