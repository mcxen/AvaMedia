# 视频下载

在主窗口选择“视频下载”，粘贴一个或多个视频链接，也可直接粘贴抖音、小红书的完整分享文本。点击“解析链接”后，列表显示标题、作者、时长和逐项错误；勾选视频，选择保存内容与目录，最后“加入下载队列”，在主窗口点击“开始”。解析和取消不会添加任务。

## 内容和网络设置

- MP4 / MKV 视频，最高画质从 360p 到 4K，或选择最佳；源画质较低时直接使用可用画质。
- MP3 / M4A 音频；音频模式禁用画质选择。
- 中文 / 英文人工字幕，可另外保存自动字幕，输出为独立 SRT 文件；没有相应字幕时不会生成字幕文件。
- 保留标题、作者等媒体元数据；播放列表 / 分P按需展开，每次最多 100 项，可逐项取消选择。
- “登录 / 代理”跳转至网络设置：选择 Firefox、Chrome、Edge、Safari、Brave 的登录态，或 Netscape 格式 cookies.txt；支持 HTTP / HTTPS / SOCKS5 代理。

程序仅在解析或下载时读取用户明确选择的登录态；cookies.txt 使用临时副本，原文件不被下载引擎改写。任务会保存所选浏览器名称、文件路径及代理设置以便恢复，日志隐藏 Cookie、Authorization、URL 查询参数和代理账号密码。

解析与实际下载使用同一套选项。已确认任务各自保留设置快照，可同时排队，不共用可变编辑参数。停止 / 重试通过固定的 `.avamedia-download-任务ID` 暂存目录继续下载；下载、合并与整理完成后才移动到最终文件名。不覆盖已有媒体或字幕，重复标题自动编号。失败和停止保留未完成数据；删除任务后，可自行移除对应暂存目录。

## 平台说明

| 平台 | 使用方式与限制 |
|---|---|
| YouTube | 单视频和播放列表；电脑须能访问视频服务，必要时设置代理；部分视频要求登录或验证。内置 QuickJS-NG 为 yt-dlp 的 YouTube JavaScript 解析提供运行时。 |
| 哔哩哔哩 | 视频页、分享短链和分P；可用清晰度取决于登录账号的观看权限。 |
| 抖音 | 粘贴视频分享链接；站点可能要求新的浏览器登录态或验证。 |
| 小红书 | 视频笔记，保留完整分享链接中的 xsec_token 等签名参数；图文笔记不在此流程中。 |

窗口对网络不可达、登录要求、浏览器 Cookie 解密、格式不可用和不支持的链接分别提供说明。Chrome / Edge 登录态读取受系统加密机制影响，失败时可尝试 Firefox 或 cookies.txt。正在直播的视频不加入此下载流程。网站规则与访问状态变化时仍可能失败，内置引擎不等于全部视频可下载。

普通 YouTube 公开视频通常可匿名下载，默认“不读取登录态”。只有遇到站点验证或需要账号观看的内容时才选择登录态；网络出口受到风控时，公开内容也可能被要求验证。调整登录或代理后可用“重试失败项”，保留已成功的视频和勾选状态。

## 开源 GUI 调研与取舍

参考 [Parabolic](https://github.com/NickvisionApps/Parabolic) 的“解析后选择视频 / 音频、字幕与下载选项”流程，以及 [Media Downloader](https://github.com/mhogomchungu/media-downloader) 的多链接、播放列表与任务队列组织。采用 AvaMedia 现有窗口、队列、组件和皮肤，独立实现界面与逻辑。

网络参数、格式选择、续传和字幕参数依据 [yt-dlp 官方说明](https://github.com/yt-dlp/yt-dlp)；YouTube JavaScript 运行时依据 [官方 EJS 指南](https://github.com/yt-dlp/yt-dlp/wiki/EJS)。平台差异核对官方 [Bilibili](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/bilibili.py)、[Douyin](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/tiktok.py)、[Xiaohongshu](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/xiaohongshu.py) extractor；登录说明依据 [Extractors 指南](https://github.com/yt-dlp/yt-dlp/wiki/Extractors)。

## 打包与验证

Windows x64 和 macOS ARM64 的发布目录包含官方 yt-dlp 2026.08.19 与 QuickJS-NG 0.17.0。Windows 使用固定哈希的官方运行器；Mac 从固定 SHA256 的同版本源码构建 ARM64 运行器，指定 macOS 13.4 部署目标，不使用最低要求 macOS 26 的官方 Mac 二进制。来源、版本、构建信息、文件尺寸与摘要记录在 `tools/download-tools.json`；许可证随 `licenses/download-tools/` 分发。无需另行安装 yt-dlp、Python 或 JavaScript 运行时。媒体合并、封装与音频提取默认使用安装包内置的 FFmpeg / FFprobe，也可在选项中指定自定义路径。

`scripts/Bundle-DownloadTools.ps1` 可单独准备开发工具；`scripts/Publish.ps1` 自动执行。用户指定的 yt-dlp 路径继续优先，官方构建包含 EJS 脚本，不启用远程脚本组件。

只打包 `qjs` / `qjs.exe` 运行器，不包含 JavaScript 编译器、SDK 或 Deno。解析和下载均先传入 `--no-js-runtimes` 清除 yt-dlp 默认运行时，再使用 `--js-runtimes quickjs:绝对路径`；系统里已有的 Deno 不会优先执行。采用 QuickJS-NG 0.17.0，满足官方建议的 0.12.0 及以上版本；官方 yt-dlp 已包含配套 EJS 脚本。Windows 原始运行器约 2.05 MiB；Mac 使用 MinSizeRel 构建并去除调试符号、关闭额外分配器，尺寸以原生构建的 `build.json` 为准。当前仍为开发版，直接使用当前运行器和清单；不维护旧运行器清理、旧安装升级或迁移分支。发布使用空输出目录，避免混入其它构建的文件。

Mac 发布须在 ARM64 Mac 上安装 CMake 后执行 `scripts/Publish.ps1`。`scripts/macos/Build-QuickJS.ps1` 核对源码哈希，检查生成文件的 ARM64 架构、部署版本、系统动态库、签名和版本，并实际执行 Promise 示例；`-SourceOnly` 可在其它平台校验源码。原生工作流保存运行器和构建清单，归档检查再次核对最低系统版本。不能在 Windows 上用官方 Mac 运行器替代此构建。

2026-10-06 的 Windows 本地 QuickJS 预览包：安装包 74.54 MiB、ZIP 92.52 MiB、解包目录 172.30 MiB；包含自带 .NET、yt-dlp 和 QuickJS-NG，FFmpeg / FFprobe 仍独立配置。运行器替换完成相关编译、脚本静态检查及安装器编译，未重新执行下载功能回归。这是本地构建记录，不是后续版本的固定体积承诺。

专项入口：`tests/AvaMedia.DownloadTests`。覆盖分享文本、签名链接、播放列表、参数校验、设置持久化、文件名冲突、日志脱敏、Cookie 副本清理、停止 / 重试、字幕及确认后入队。使用真实 yt-dlp 对本机 HTTP 视频下载，FFmpeg 实际生成 MP4 / MKV / MP3 / M4A，并检查媒体流和时长；三套皮肤渲染默认与最小窗口。公网平台测试单独记录，不作为离线测试的必经条件。

2026-10-06 在当前网络、无登录态下做四站元数据检查：B站官方 extractor 样例解析成功；YouTube 提示登录确认，抖音提示需要新 Cookie，小红书无签名旧样例未返回视频格式。后三站尚未验证用户登录态下的实际下载，界面已针对这些结果提供登录态、完整签名链接和代理入口。未自动读取或使用用户浏览器资料。
