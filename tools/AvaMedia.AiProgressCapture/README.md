# AI progress capture

Captures the existing Chinese desktop windows using deterministic in-memory jobs,
subtitle cues, activity histories, six speech segments (segment three running),
two waiting subtitle jobs, and a classification snapshot with 412 of 1284 files
processed. Solid-color thumbnails and a synthetic audio envelope are fixtures;
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
