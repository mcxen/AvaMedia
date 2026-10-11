# 安装与连接

## 安装 AvaMedia

使用 [官方 Releases](https://github.com/mcxen/AvaMedia/releases)，按本机系统/架构选择实际存在的 DMG、Windows 安装 EXE 或便携 ZIP，核对发布的 SHA256。不要凭版本号拼接下载 URL，不把源代码归档当可执行应用。最新资产和工具可用性现场查询，不写死历史版本。架构支持以实际资产列表为准。

macOS 挂载 DMG 后将 `.app` 安装到可写应用目录；Windows 可解压便携包或使用安装器。保留现有安装和正在运行的编辑任务；安装到独立目录可以避免打断当前应用。若正式包还没有所需工具，清楚说明，不能把已有端点称为新版本。

应用可执行文件：macOS 通常为 `天池万象转换.app/Contents/MacOS/AvaMedia.Desktop`；Windows 为安装/解压目录内的 `AvaMedia.Desktop.exe`。以实际路径为准。源码开发构建可用 `dotnet /absolute/path/AvaMedia.Desktop.dll`，需要相应 .NET/AspNetCore 运行时，不能替代安装验收。

## 无窗口启动

默认端点 `http://127.0.0.1:18920/mcp`，无令牌、无目录白名单。未明确要求时保持本机访问。

```sh
"/absolute/path/to/AvaMedia.Desktop" --mcp-background --mcp-port 18920
```

这一入口不显示主窗口，沿用正常引擎、模型及队列存储。运行宿主需要本地桌面会话，并非无桌面系统的守护进程；退出/重启系统会结束它。`SIGTERM` / Ctrl+C 会走保存和退出流程。没有 MCP 请求、活动任务或打开的任务编辑，并持续安静 2 分钟后，后台宿主也会保存队列并自行退出，避免调用方结束后继续占用端口和发布目录。

已运行 AvaMedia 的工作区复用其 MCP；未开启时用「选项 → MCP」开启。不要启动两个宿主同时写默认工作区。需要独立宿主时指定 `--mcp-state-directory /absolute/path/state` 和不同端口；此参数隔离设置/队列，模型与 AI 检查点仍由应用共享。

脚本可以先检查再启动，并保存独立的进程日志：

```sh
python3 scripts/setup_mcp.py --executable "/absolute/path/to/AvaMedia.Desktop" --start --client codex
```

开发 DLL 使用 `--executable /absolute/path/AvaMedia.Desktop.dll --dotnet /absolute/path/dotnet`。可用 `--url` 改端口，`--state-directory` 指定隔离工作区，`--log-directory` 选日志目录。脚本不下载/替换应用，也不主动停止现有进程。

## 配置 AI 客户端

Codex：

```sh
codex mcp add avamedia --url http://127.0.0.1:18920/mcp
codex mcp get avamedia
```

也可配置 `~/.codex/config.toml` 的 `[mcp_servers.avamedia]`，`url` 为上述地址。使用 CLI 可以保留其它配置。配置方式参考 [OpenAI Docs](https://developers.openai.com/learn/docs-mcp)。当前会话没加载新工具时，用下方直接调用脚本继续，之后新开会话或刷新客户端连接。

Claude Code 可用脚本 `--client claude`，或 `claude mcp add --transport http --scope user avamedia http://127.0.0.1:18920/mcp`。Cursor 配置 `mcpServers.avamedia.url`；只合并这一项，保留其它服务器配置。

将整个 `avamedia-mcp` 技能目录复制到 AI 客户端支持的用户技能目录（Codex 当前支持 `~/.agents/skills`，部分本地安装也读取 `~/.codex/skills`），或仓库的 `.agents/skills`，不要只复制 SKILL.md 而漏掉脚本和参考文件。[OpenAI 技能文档](https://learn.chatgpt.com/docs/build-skills)。应用发布目录附带 `skills/avamedia-mcp`；macOS 位于 `.app/Contents/MacOS/skills/avamedia-mcp`。

## 直接调用和排障

```sh
python3 scripts/mcp_client.py tools
python3 scripts/mcp_client.py call avamedia_capabilities
python3 scripts/mcp_client.py call avamedia_get_runtime
python3 scripts/mcp_client.py call avamedia_classify_media --arguments-file /absolute/path/request.json
```

脚本使用应用当前 SDK 的 `2026-07-28` 无状态协议、方法请求头和 `_meta`；无需手写 initialize。MCP 客户端 SDK 通常自行处理协商；不要将 initialize 返回“无需初始化”误判为服务关闭。端点可访问但缺少工具时先核对宿主版本/启动路径，不要反复提交任务。客户端请求超时后按原 `requestId` 恢复提交。
