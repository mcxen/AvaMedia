# AI progress capture

Captures the existing Chinese desktop windows using deterministic in-memory jobs,
subtitle cues, activity histories, six speech segments (segment three running),
two waiting subtitle jobs, and a classification snapshot with 412 of 1284 files
processed. Solid-color thumbnails and a synthetic audio envelope are fixtures;
the tool does not submit tasks or run AI inference. Settings and fixture files
are isolated in a temporary directory and removed on exit.

An X server must be available at `:11`. The harness sets scale 1 and renders client
areas using `RenderTargetBitmap`, overriding window minimum sizes only in the
capture process. It preserves the current application layout, including any
clipping at the requested demo sizes.

If the environment blocks X11 sockets, pass `--headless` after the output path
to render the same controls with Avalonia Headless and Skia. This mode is recorded
in `captures.json`; it does not claim native X11 window verification. In a sandbox
where the review directory is read-only, use `/tmp/avamedia-ai-progress-v2/impl`
and pass `--demos /workspace/AvaMedia-sidebar-review/ai-progress/v2` to the Python script.

```sh
~/.dotnet/dotnet build tools/AvaMedia.AiProgressCapture -p:EnableWindowsTargeting=true
~/.dotnet/dotnet tools/AvaMedia.AiProgressCapture/bin/Debug/net8.0/AvaMedia.AiProgressCapture.dll /workspace/AvaMedia-sidebar-review/ai-progress/v2/impl
python3 tools/AvaMedia.AiProgressCapture/sidebyside.py /workspace/AvaMedia-sidebar-review/ai-progress/v2/impl
```

The capture outputs are `subtitle-submit-desktop.png` (752 × 448),
`subtitle-progress-desktop.png` (848 × 560),
`progress-from-tasklist-desktop.png` (832 × 552), and `captures.json`.

`sidebyside.py` requires Pillow (`python3 -m pip install --user pillow` if missing).
It checks all six demo names, skips those without an implementation PNG, and
writes `compare-<name>.png` with demo on the left and implementation on the right.
The implementation remains at native size; the full demo is resized
proportionally to the same height. Demo PNGs default to the output directory's
parent, or can be specified with `--demos`.
