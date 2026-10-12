# Subtitle web capture

From the repository root, run:

```sh
bash tools/web-capture/run.sh
```

Publishes `src/AvaMedia.Browser` in Release with `EnableWindowsTargeting=true` to `/tmp/avam-web`, serves its unmodified `wwwroot` through `mock_server.py` on loopback, then uses Node/Playwright and Chrome to capture the actual Avalonia UI in `zh-CN`. The server stops on exit; logs remain in the printed `/tmp/avam-web-capture.*` directory. No git commands or desktop source edits are performed.

Outputs default to `/workspace/AvaMedia-sidebar-review/ai-progress/v2/impl/`:

- `subtitle-submit-web.png` — 1280 × 800, three selected server files and subtitle settings.
- `subtitle-progress-web.png` — 1280 × 800, running subtitle task.
- `subtitle-progress-mobile.png` — 390 × 844, the same task after resizing.
- `capture-report.json` — dimensions, nonblank pixel checks, observed MCP calls and fixture state. Open all three PNGs for final visual review; pixel checks alone do not establish that the correct page is visible.

The fixture offers only the subtitle feature so it is selected when opening “文件与任务下发”. Navigation uses actual canvas clicks: open the creation tab, open “添加服务器文件…”, select the file list with Ctrl+A and confirm with Enter, return to tasks, and click the running task. There are no hash routes or DOM accessibility controls in this build. Click coordinates are derived from the source layout at DPR 1; if layout changes, adjust `CAPTURE_POINTS` (a JSON object with `createTab`, `addFiles`, `pickerFirstRow`, `tasksTab`, or `runningTask` coordinate pairs).

The mock implements MCP JSON-RPC initialization, tool listing, capabilities, runtime information, directory browsing, task listing/detail/log and model information. Unknown tools fail explicitly. The fixed snapshot has one running job at 46%, segment 3/6, 186 Chinese cues, and two waiting subtitle jobs. `/web/tasks/{id}/ai/progress`, `segments`, `waveform?buckets=…` and `results?after=…` follow `AiProgressContract.cs` / `WebAiProgress.cs`, including zero-based segment indexing and incremental cues. `preview` returns 404, as the subtitle activity has no preview (`hasPreview=false`). No real media processing or task control is performed.

Prerequisites: .NET 8 with wasm-tools, Python 3, Node, Playwright, and Chrome/Chromium. Playwright is resolved from `PLAYWRIGHT_MODULE`, the normal Node module path, or this workspace's existing `/workspace/bp-shots/node_modules/playwright`. Chrome defaults to `/usr/bin/google-chrome`, otherwise Playwright's installed Chromium. No dependencies are downloaded by the capture scripts.

Optional environment settings:

| Variable | Purpose |
| --- | --- |
| `CAPTURE_OUTPUT` | Writable screenshot destination |
| `CAPTURE_SKIP_PUBLISH=1` | Reuse an existing browser publish |
| `CAPTURE_NO_RESTORE=1` | Use existing restored NuGet assets |
| `DOTNET_BIN` | Override `~/.dotnet/dotnet` |
| `CAPTURE_PUBLISH_DIR` | Override `/tmp/avam-web` |
| `CAPTURE_PORT` | Override loopback port 8765 |
| `CHROME_PATH` | Explicit Chrome or cached Playwright Chromium executable |
| `PLAYWRIGHT_MODULE` | Explicit Playwright module directory |
| `CAPTURE_POINTS` | Override canvas click coordinates after a UI change |

For a writable temporary output and an existing publish:

```sh
CAPTURE_OUTPUT=/tmp/avam-web-capture/impl CAPTURE_SKIP_PUBLISH=1 bash tools/web-capture/run.sh
```

## Execution status in run F

Release publish succeeded on 2026-10-12 using `--no-restore -m:1` after the default parallel invocation exited without a compiler diagnostic. Existing nullable/unused-field, trimming and NuGet vulnerability-feed warnings were reported; zero errors. Python, Node and shell syntax checks passed.

Capture execution is **blocked and visually unverified** in the supplied sandbox: the requested output directory is read-only; creating the Python HTTP socket fails with `PermissionError: [Errno 1] Operation not permitted`; Chrome also exits at socket setup. No PNGs were generated, no image inspection was possible, and canvas coordinates have not been calibrated in a running browser. Run the command in an environment permitting loopback sockets, Chrome and writes to the output directory before treating screenshots as reviewed.
