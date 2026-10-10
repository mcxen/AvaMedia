# WebView 网页嗅探检查（2026-10-08）

在 macOS 15.7.1 / ARM64 上，用当前源码的 Avalonia NativeWebView（WKWebView）打开四个公开页面，检查播放、资源列表、媒体上下文和下载任务构造。测试窗口使用独立应用标识，任务写入本地验证目录，不执行下载、不修改日常任务队列。

## 实测结果

| 页面 | 播放与嗅探 | 下载任务接入 |
| --- | --- | --- |
| [MDN video 示例](https://interactive-examples.mdn.mozilla.net/pages/tabbed/video.html) | srcdoc iframe 内的视频可播放；识别 flower.webm 和 flower.mp4 两个地址，保留约 5.076 秒时长和 HTTP Referer | WEBM 地址带回下载窗口并构造任务；来源页与媒体地址分开保存 |
| [hls.js demo](https://hlsjs.video-dev.org/demo/) | 默认 Big Buck Bunny 可播放；识别主 M3U8 和两个实际请求的子 M3U8，没有列出 TS 分片 | 主清单带回并构造任务 |
| [dash.js Reference Client](https://reference.dashif.org/dash.js/latest/samples/dash-if-reference-player/index.html) | 点击 Load 后视频可播放；识别 bbb_30fps.mpd，过滤 M4V 分片后仅保留一个 MPD；Origin 为 https://reference.dashif.org | MPD 带回并构造任务 |
| [Wikimedia Big Buck Bunny](https://commons.wikimedia.org/wiki/File:Big_Buck_Bunny_4K.webm) | 页面和播放器可用；识别四个 WEBM 地址和一个 MOV 地址；不再把同名 HTML 网页列为视频 | 默认的 480p WEBM 地址带回并构造任务 |

所有任务保留页面 URL、实际媒体 URL、Referer、User-Agent、媒体类型及 Cookie 快照 ID。DASH 样本确认了跨域 Origin；HLS 样本的 Origin 为空，不能据此声称捕获了完整请求头。Cookie 快照写入和回传已检查，没有测试账户登录、鉴权或下载器的 Cookie 重用。

## 本次发现并修复

- srcdoc iframe 的 about:srcdoc 不能作为 HTTP Referer，导致可播放的视频被丢弃。使用 document.baseURI 解析资源，在非 HTTP 框架中回退到父页 Referer。
- DASH 的 M4V 分片被按文件后缀误当作完整视频，样本中增长到 22 项。从已获取的清单解析 SegmentTemplate / SegmentList 和 HLS 分片地址，排除已知分片并移除先前误列的条目。读取 fetch 响应的有界副本或 XHR 已有响应，不额外重放清单请求。
- 资源记录的空 Origin、零时长覆盖了 fetch/XHR 或 video 元素的元信息。合并同一 URL 的已知信息；DASH Origin 和 MDN 时长已复验。
- WKWebView 的原生事件只提供导航请求，以 .webm 结尾的 Wikimedia HTML 页面被误列并默认选中。macOS 使用脚本确认媒体子资源；Windows 原生请求入口忽略接受 HTML 的请求。

## 与开源工具的对齐情况

以下是 AvaMedia 实测与对方官方文档的能力对照。没有安装扩展做同页面对跑，也没有统计网站成功率。

| 能力 | AvaMedia 当前状态 | 开源工具参考 |
| --- | --- | --- |
| HTTP 视频、iframe、HLS / DASH 地址识别 | 四个样本通过；能回传来源页及下载上下文 | [猫抓](https://cat-catch.94cat.com/)提供资源嗅探、类型/正则筛选及 HLS / DASH 解析；[HLS Downloader](https://github.com/puemos/hls-downloader/blob/master/FUNCTIONALITIES.md)的嗅探集中于 HLS |
| 清单画质、音轨和字幕选择 | 下载设置提供最高画质等参数，但嗅探列表没有展示真实分辨率、码率、帧率、主/子清单关系及独立音轨；同页资源标题相同，难以区分 | HLS Downloader 文档提供清单层级、分辨率/码率/FPS、音轨/字幕选择；[猫抓 DASH 解析器](https://cat-catch.94cat.com/docs/mpdparse)提供画质与音频选择 |
| 隐藏地址、纯 blob、缓存数据 | 只收集可识别的 HTTP 地址/清单；没有 JSON 深度搜索、SourceBuffer 缓存导出或录制 | [猫抓深度搜索 / 缓存捕捉](https://cat-catch.94cat.com/docs/cache-capture)提供隐藏清单搜索及缓存捕捉 |
| 嗅探资源管理 | 完整 URL 去重、选择和数量上限；没有搜索、类型过滤、复制地址等操作 | 猫抓提供类型/正则配置；HLS Downloader 文档提供过滤、复制 URL 和清空 |

后续优先补齐资源地址辨识、主/子清单归组和真实画质/音轨信息，再考虑可选的深度搜索。单靠更多文件后缀不足以解决 Worker、自定义协议、纯 MSE 缓存或受保护媒体。

## 验证边界与证据

本地记录在 `artifacts/browser-sniff-acceptance-20261008/`：`mdn-final.json`、`hls-final.json`、`dash-browser.json`（修复前）、`dash-filtered.json`、`wikimedia-browser.json`（修复前）、`wikimedia-filtered.json`、`queue-*.json`。验证程序、应用包和原始记录属于忽略的本地产物。

共享工作区其他未完成源码曾阻止整体编译；网页检查使用已提交基线 e5b0368 加本次两个源码文件的独立副本。交付前另基于最新已提交代码 83951e2 加本次修改完成 Desktop/Core 编译，0 警告、0 错误；JavaScript 语法与 diff 检查通过。没有修改其他任务的源码。

本次没有进行完整视频下载、转封装输出、账户登录、DRM、Service Worker、直播或 Windows WebView2 验收。四个样本只能证明这些页面的基本链路，不代表已全面对齐猫抓或 HLS Downloader。
