# 更新网络

`GitHubUpdateRoutes` 为正式 Release 检查和安装包下载提供官方线路与 10 个预设 HTTPS 加速入口。`HttpClient` 使用系统／环境代理设置，无需另填 GitHub 令牌。只请求公开的 `mcxen/AvaMedia` 更新资源。

来源：[tvv.tw 服务说明](https://tvv.tw/)、[GH-Proxy 使用文档](https://gh-proxy.com/docs/github-accelerator)、[GH-Proxy 官方前端的节点配置](https://gh-proxy.com/_astro/Converter.CIXNQ_XG.js)。其中 9 个入口属于 GH-Proxy 的域名和 CDN 节点，服务故障可能影响多个入口。

## 2026-10-10 入口探测

按用户要求，从开发机对真实的 `https://api.github.com/repos/mcxen/AvaMedia/releases/latest` 发起限时 GET，验证正式版本号、仓库发布页和 assets；官方入口实测返回 HTTP 403 / API rate limit exceeded。以下 10 个入口均返回 HTTP 200 和有效 Release JSON。耗时是本次完整响应时间，包含连接和传输，不代表其他用户网络的速度。两批请求期间正式 Release 从 v1.1.62 更新到 v1.1.63。

| 加速入口 | Release 耗时 | 返回版本 | 安装包前 64 KiB |
| --- | ---: | --- | --- |
| `https://tvv.tw/` | 2.691 s | v1.1.62 | HTTP 206，65,536 B |
| `https://gh-proxy.com/` | 2.078 s | v1.1.62 | HTTP 206，65,536 B |
| `https://gh-proxy.org/` | 2.238 s | v1.1.62 | HTTP 206，65,536 B |
| `https://hk.gh-proxy.com/` | 2.940 s | v1.1.62 | HTTP 206，65,536 B |
| `https://cdn.gh-proxy.com/` | 2.992 s | v1.1.62 | HTTP 206，65,536 B |
| `https://v6.gh-proxy.com/` | 1.527 s | v1.1.63 | HTTP 206，65,536 B |
| `https://v4.gh-proxy.org/` | 1.050 s | v1.1.63 | HTTP 206，65,536 B |
| `https://v6.gh-proxy.org/` | 0.789 s | v1.1.63 | HTTP 206，65,536 B |
| `https://cdn.gh-proxy.org/` | 0.787 s | v1.1.63 | HTTP 206，65,536 B |
| `https://axisnow.gh-proxy.org/` | 1.027 s | v1.1.63 | HTTP 206，65,536 B |

安装包探测使用 `AvaMedia-1.1.62-osx-arm64.dmg`、`Range: bytes=0-65535`，限时且限制响应大小。只验证文件下载入口；未执行完整安装包下载、应用退出替换或跨平台安装验收。

第二批 5 个入口的这段文件 SHA256 均为 `0c348888fd84d9142272a8effbac81998eb322add567e0970225888270d41a87`；这是 64 KiB 分段的哈希。应用实际下载完成后仍检查完整安装包的 Release SHA256。

未预置 `ghproxy.homeboyc.cn`、`github.moeyy.xyz` 等本次拒绝请求或连接失败的入口；`ghfast.top`、`ghproxy.net` 的分段下载成功，但 Release API 返回 403，当前优先采用已验证能读取 Release 的入口。

## 重试与线路缓存

检查请求每隔 350 毫秒启动一条备用线路，最多同时请求 3 条，每条限时 8 秒。有效结果到达后再收集 750 毫秒，选择其中最新的正式版本，取消其余请求。发布页必须精确指向本仓库的版本 tag，安装包 URL 和名称仍按平台、版本严格匹配；JSON 最多 2 MiB。网页、广告或错误 JSON 会触发下一线路。成功检查缓存 5 分钟；全线路失败只缓存 1 分钟，由应用继续后台重试。

失败和限流线路暂时冷却，遵循该线路的 `Retry-After` 和 `x-ratelimit-reset`，不阻塞其他线路。检查失败后的后台间隔为 1、2、4、5 分钟，之后保持 5 分钟；恢复后更新原通知，并按现有自动／静默更新设置处理新版本。应用退出取消请求和等待。

下载依次尝试可用线路，响应头限时 8 秒，连续 30 秒未收到文件数据时换线路；切换时重新下载并重新计算 SHA256。全部网络线路不可用时采用相同后台重试间隔，通知显示重试状态，停止下载会取消等待和请求。校验失败仍尝试其他线路，最终无有效包时报告校验错误；目录权限、磁盘写入及安装准备错误仍作为实际失败处理。

线路记录保存在用户数据目录 `AvaMedia/Updates/routes.json`，有效期 24 小时。检查按完整响应延迟排序，下载按每 MiB 传输耗时排序；首次下载可以参考已成功检查的线路。失效线路重新尝试后更新记录，缓存读写错误不阻止更新。缓存不包含令牌、代理凭据或下载内容。
