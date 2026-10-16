# Linux 版本

Release 工作流的 `linux` job 在 ubuntu-24.04 上运行 `scripts/Package-Linux.sh <版本>`，生成 `AvaMedia-<版本>-linux-x64.tar.gz` 并随主 Release 发布。

- 自包含（self-contained）的 .NET 8 发布，不需要另装 .NET 运行时；与 Windows / macOS 使用同一版本号和 `AvaMedia-<版本>-<RID>` 命名。
- 解压后目录包含 `avamedia` 启动脚本、`lib/`（程序本体）、`avamedia.desktop`、`avamedia.png`、许可证和文档。可将 `avamedia.desktop` 复制到 `~/.local/share/applications/` 并把 `Exec` 指向启动脚本。
- 不内置媒体工具。需要自行提供 FFmpeg / FFprobe 9.x（缩略图使用 `reset_sar` 等选项）、yt-dlp，以及要求 JavaScript 运行时的下载所需的 QuickJS-NG；可通过 PATH 或 `AVAMEDIA_FFMPEG`、`AVAMEDIA_FFPROBE`、`AVAMEDIA_YT-DLP`、`AVAMEDIA_QJS` 指定。
- 窗口框架保留 `SystemDecorations.Full`，由窗口管理器提供装饰、缩放和阴影；`AVAMEDIA_CHROME` 已停止读取。详见 [窗口框架](WINDOW-CHROME.md)。
- 内嵌声音预览和浏览器嗅探当前仅接入 Windows / macOS；系统背景、回收站等集成也有平台限制。Linux 包的存在不表示已完成桌面功能对等验收。
- CI 使用 `xvfb-run` 执行 `--capture` 启动检查；未在真实发行版桌面上做安装验收。
