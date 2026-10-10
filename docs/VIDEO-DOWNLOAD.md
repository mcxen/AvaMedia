# 视频下载

在主窗口选择“视频下载”，粘贴一个或多个视频链接，也可直接粘贴抖音、小红书的完整分享文本。点击“解析链接”后，列表显示标题、作者、时长和逐项错误；勾选视频，选择保存内容与目录，最后“加入下载队列”，在主窗口点击“开始”。解析和取消不会添加任务。

## 内嵌浏览器嗅探

点击“浏览器嗅探”在应用内打开网站；已有链接时打开第一项，没有链接时在地址栏输入网站地址。可后退、前进、刷新，也可直接在网页登录、完成验证及播放视频。右侧持续显示识别到的媒体，选择后点击“使用选中视频”，回到下载窗口选择格式、保存位置并加入队列。编辑已有下载任务时只允许选择一个媒体。

浏览器使用 `Avalonia.Controls.WebView` 11.4.1（MIT）：Windows 使用 Edge WebView2，macOS 使用 WKWebView。Windows 未安装 WebView2 Runtime 时提供官方安装页面入口，安装后重新打开嗅探窗口；macOS 使用系统组件。当前内嵌嗅探入口限定为 Windows、macOS。浏览器使用系统网络配置，下载任务仍使用“登录与网络”中设置的下载代理。

在导航前安装文档开始脚本，覆盖子框架；结合原生请求事件、fetch/XHR 响应 MIME、video/source 元素和 Performance 资源记录持续采集，识别 MP4、WebM、HLS/M3U8 和 DASH/MPD。排除常见 TS、M2TS、M4S、AAC 分片，并从已获取的 HLS/DASH 清单识别其他后缀的分片；按完整媒体 URL 去重，最多显示 100 项，保留签名查询参数。HLS/DASH 可选择最高画质与清单提供的字幕，普通文件保持原始画质。页面哈希路由保留到来源页地址中。

选中媒体保留来源页、实际媒体 URL、Referer、User-Agent 和可获取的 Origin。登录 Cookie 保存为应用本地的独立快照，队列只记录快照 ID；每次下载使用临时副本，结束后删除副本。关闭浏览器窗口不影响快照，停止、重试及重新启动客户端仍可使用；取消下载设置时删除本次未使用的快照。选择其他登录态或“不读取登录态”时不使用快照。导入其他机器的任务须重新嗅探以取得登录态。

WKWebView 的原生事件仅提供导航请求，子资源依靠文档开始脚本与资源记录补充；无法保证捕获所有 Service Worker、自定义播放器或未暴露 HTTP 地址的媒体。blob 地址本身不加入队列，只收集背后可识别的 HTTP 媒体或清单。检测到页面加密媒体事件时阻止选择，不支持 DRM 下载。媒体签名和服务器登录状态仍可能过期；出现过期提示时编辑任务并再次嗅探，保留来源页以便重新打开。网站也可能限制内嵌浏览器登录或访问。

2026-10-08 已完成 macOS 原生 WebView 的四个公开页面播放、嗅探与下载任务接入检查，详见[网页嗅探检查](acceptance/WEBVIEW-SNIFFING-CHECK-2026-10-08.md)。Desktop/Core 编译和相关静态检查通过；未进行网站登录、完整媒体下载或 Windows WebView2 验收。

## 链接解析与外部浏览器

窗口只保留链接输入、视频选择和输出选项。系统标题栏承担标题，站点名单、平台提示、引擎版本及重复数量说明已移除；已知元数据才显示，来源地址在标题详情中脱敏显示。列表只用复选框选择，取消勾选即可排除；多项时显示全选，失败时显示重试。解析按钮原位切换为取消，修改链接清除旧结果。默认窗口为 1040 × 740，最小 860 × 620，列表与设置独立滚动，输出与确认固定在底部。

MP4、WebM 等视频直链粘贴后自动解析，以文件名生成下载条目。解析阶段不请求网页或 HEAD，不因网页解析失败而阻止加入队列；下载使用随应用提供的 `AvaMediaDirect` 解析器直接交给 yt-dlp HTTP 下载器，保留原始画质、代理、续传和格式整理。自动解析只确认链接类型，文件是否可访问在实际下载时确认；HTTP 520 等服务端错误仍可能导致失败。

`fileditchfiles.st` 的 `.mp4` 地址实际返回播放器网页，按 Fileditch 页面处理，不能直接保存为媒体。粘贴后自动通过 CDP 等待网页自身的浏览器验证完成，读取 video/source 中带签名的真实 CDN 地址；本机 CDP 未连接时启动已安装的 Chrome / Edge，使用 AvaMedia 独立用户目录和本机端口，不读取日常浏览器资料。未找到浏览器或网页需要用户验证时显示原因。队列保存稳定的原始页面链接；每次下载、重试先重新加载该页并获取签名，避免过期 CDN 地址，同时以稳定页面和参数绑定续传目录。

2026-10-07 对用户指定的 Fileditch 地址做限定验证：原始地址普通 GET 返回 HTTP 520，浏览器返回播放器网页；解析出的真实媒体地址返回 HTTP 206、`video/mp4`，4096 字节样本包含正确 `ftyp` 文件头，Content-Range 总长度为 1711308934 字节。Core 实际调用也通过独立 CDP 浏览器自动解析，并由现有下载执行器取得 10241 字节的正确 MP4 样本，队列保留原页面。没有下载完整文件。实际验证还发现并修正 `--use-extractors` 参数误用 Python 类名的问题，改为插件的 `IE_NAME`。

“从外部浏览器识别”通过本机 CDP 读取已打开页面的 video/source、资源记录和识别期间的网络媒体响应，支持 MP4 等文件及 M3U8 / MPD。粘贴网页链接时只识别匹配页面，粘贴直链时只保留该媒体地址；输入为空时最多检查 30 个标签页、列出 100 项。识别结果自动勾选，可逐项选择并加入现有队列，不自动开始下载、不刷新页面或触发播放。

CDP 默认地址为 `http://127.0.0.1:9222`，可在下载网络设置中修改，仅接受本机地址。Fileditch 自动解析会在需要时启动独立 CDP 浏览器；手动“从外部浏览器识别”使用已连接的浏览器。例如 Mac 可执行 `open -na "Google Chrome" --args --remote-debugging-port=9222 --user-data-dir="$TMPDIR/AvaMedia-CDP"`，Windows 可使用 `chrome.exe --remote-debugging-port=9222 --user-data-dir="%LOCALAPPDATA%\AvaMedia\CDP"`，然后在该独立浏览器打开视频。普通浏览器窗口未启用 CDP 时不会被接管。Chrome 要求使用独立用户目录，依据 [Chrome 远程调试说明](https://developer.chrome.com/blog/remote-debugging-port)。

CDP 识别保留媒体地址、来源页、User-Agent 和本机连接信息；识别后可选择“浏览器 CDP”登录态，开始下载时通过 [Network.getCookies](https://chromedevtools.github.io/devtools-protocol/tot/Network/#method-getCookies) 只读取匹配媒体地址的 Cookie，写入临时副本，结束后移除，不写入队列或日志。不能用 Netscape 文件表达的分区 Cookie 不导出。选择“不读取登录态”、其他浏览器或 cookies.txt 时不读取 CDP Cookie。使用 CDP 登录态时须保留原标签页；链接签名过期后须重新识别。受 DRM 保护的媒体、未暴露 HTTP 来源的 blob、关闭的标签页仍不支持直接下载。

## 内容和网络设置

- MP4 / MKV 视频，最高画质从 360p 到 4K，或选择最佳；源画质较低时直接使用可用画质。
- MP3 / M4A 音频；音频模式隐藏画质选择。直链、Fileditch、Bunkr 与 Pixeldrain 使用原始文件，隐藏不适用的画质和字幕选项。
- 中文 / 英文人工字幕，可另外保存自动字幕，输出为独立 SRT 文件；没有相应字幕时不会生成字幕文件。
- 保留标题、作者等媒体元数据；播放列表 / 分P按需展开，每次最多 100 项，可逐项取消选择。
- “登录与网络”按需展开；已有非默认网络设置的任务编辑时自动展开。可选择 Firefox、Chrome、Edge、Safari、Brave 的登录态，或 Netscape 格式 cookies.txt；支持 HTTP / HTTPS / SOCKS5 代理与本机 CDP 地址。

程序仅在解析或下载时读取用户明确选择的登录态；cookies.txt 使用临时副本，原文件不被下载引擎改写。任务会保存所选浏览器名称、文件路径及代理设置以便恢复，日志隐藏 Cookie、Authorization、URL 查询参数和代理账号密码。

解析与实际下载使用同一套选项。已确认任务各自保留设置快照，可同时排队，不共用可变编辑参数。停止 / 重试通过固定的 `.avamedia-download-任务ID` 暂存目录继续下载；下载、合并与整理完成后才移动到最终文件名。不覆盖已有媒体或字幕，重复标题自动编号。失败和停止保留未完成数据；删除任务后，可自行移除对应暂存目录。

## 平台说明

| 平台 | 使用方式与限制 |
|---|---|
| YouTube | 单视频和播放列表；电脑须能访问视频服务，必要时设置代理；部分视频要求登录或验证。内置 QuickJS-NG 为 yt-dlp 的 YouTube JavaScript 解析提供运行时。 |
| 哔哩哔哩 | 视频页、分享短链和分P；可用清晰度取决于登录账号的观看权限。 |
| 抖音 | 粘贴视频分享链接；站点可能要求新的浏览器登录态或验证。 |
| 小红书 | 视频笔记，保留完整分享链接中的 xsec_token 等签名参数；图文笔记不在此流程中。 |
| Bunkr | `/f/`、`/v/` 单视频与 `/a/` 相册；勾选“展开列表（最多 100 项）”后逐项选择视频。相册解析保留数字文件 ID，开始下载或停止后重试时重新请求签名，不保存过期 CDN 地址。浏览器网络模拟由内置 yt-dlp 提供；自定义引擎须包含 curl_cffi。 |
| Pixeldrain | `/u/` 单视频、`/l/` 文件列表以及对应的 `/api/file/`、`/api/list/` 链接；`#item=0` 选择列表第一项，其他序号也从 0 开始。勾选展开后列出视频，跳过图片、音频和压缩包。站点验证码、传输额度和并发限制会显示为错误提示。 |

Bunkr 和 Pixeldrain 提供原始文件画质，不按“最高画质”降采样；仍可选择 MP4 / MKV 封装或 MP3 / M4A 音频提取。站点未提供字幕时不会生成字幕。单次列表最多显示 100 个视频；任务保存成员页面链接，不保存签名直链。代理、明确选择的浏览器登录态或 cookies.txt，以及停止 / 重试续传沿用同一下载流程。

`IVideoDownloadService` 定义界面解析入口，`IVideoDownloadProvider` 补充路由匹配和任务执行，`VideoDownloadService` 在解析与下载时使用同一套有序路由：Bunkr、Pixeldrain、Fileditch、视频直链、通用 yt-dlp。CDP 已识别的普通媒体按直链处理，Fileditch 保存原始页面并在执行时重新获取媒体地址。专用站点失败时返回本站错误，避免静默转交通用解析。新服务可实现该接口并加入路由；界面及任务模型不依赖站点实现。

专用解析器位于 `src/AvaMedia.Core/DownloadPlugins/`，独立实现并随客户端复制到 `download-plugins/`，通过明确的插件目录和 extractor 名称加载，不需要安装 Python 或额外下载插件。请求经过 yt-dlp 网络层，保留 Cookie、代理及 Bunkr 浏览器网络模拟。Bunkr 协议参考 [BunkrDownloader 的 API 实现](https://github.com/Lysagxra/BunkrDownloader/blob/main/src/crawlers/api_utils.py)和[相册解析](https://github.com/Lysagxra/BunkrDownloader/blob/main/src/crawlers/crawler_utils.py)；Pixeldrain 使用[官方 API](https://pixeldrain.com/api)，不绕过验证码或账户限额。

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
