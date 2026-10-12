# Linux 版本

Release 工作流的 `linux` job 在 ubuntu-24.04 上运行 `scripts/Package-Linux.sh <版本>`，生成 `AvaMedia-<版本>-linux-x64.tar.gz` 并随主 Release 发布。

- 自包含（self-contained）的 .NET 8 发布，不需要另装 .NET 运行时；与 Windows / macOS 使用同一版本号和 `AvaMedia-<版本>-<RID>` 命名。
- 解压后目录包含 `avamedia` 启动脚本、`lib/`（程序本体）、`avamedia.desktop`、`avamedia.png`、许可证和文档。可将 `avamedia.desktop` 复制到 `~/.local/share/applications/` 并把 `Exec` 指向启动脚本。
- **不内置 FFmpeg。** 需要系统提供 FFmpeg / FFprobe 9.x（缩略图使用 `reset_sar` 等 9.x 选项，7.x 不可用），或通过环境变量 `AVAMEDIA_FFMPEG`、`AVAMEDIA_FFPROBE` 指定路径。yt-dlp 同样使用系统安装版本。
- 窗口框架：X11 无法扩展到窗口管理器装饰，浅色 / 深色皮肤去掉系统标题栏并使用自绘最小化 / 最大化 / 关闭和边缘缩放；`AVAMEDIA_CHROME=system` 可回退到系统装饰。详见 [窗口框架](WINDOW-CHROME.md)。
- CI 使用 `xvfb-run` 执行 `--capture` 启动检查；未在真实发行版桌面上做安装验收。
