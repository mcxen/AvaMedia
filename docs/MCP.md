# 远程管理与 MCP 服务

在选项页「远程管理与 MCP」启用服务并应用，复制地址或 MCP 配置即可连接。默认端口为 `18920`，本机地址为 `http://127.0.0.1:18920/mcp`。开启「允许局域网连接」后，设置页列出当前网卡地址，其他设备使用其中一个地址连接。服务随主窗口启动，缩到托盘时继续运行，正常退出时停止。

客户端可同时作为服务器。常驻入口无需显示主窗口或分类窗口，复用正常媒体引擎、模型和持久队列：

```sh
"/absolute/path/to/AvaMedia.Desktop" --server --allow-lan --mcp-port 18920
```

`--server` 保持运行直到显式退出；省略 `--allow-lan` 时沿用设置中的监听范围，默认仅本机。短时 MCP 调用仍可使用 `--mcp-background`，无活动请求和任务两分钟后自动退出。

后台宿主需要桌面会话，退出系统后结束；Ctrl+C / SIGTERM 走保存与退出流程。默认使用正常工作区；已有宿主时复用其端点，不同时启动两个进程写同一工作区。`--mcp-state-directory /absolute/path/state` 可指定独立的设置/队列目录，模型和 AI 检查点仍共享。后台模式禁用完成后弹窗、打开文件夹、音效及关机动作。

按当前产品要求，服务无需访问令牌，不限制文件目录，开放跨域访问。文件路径指运行 AvaMedia 的机器上的绝对路径；远程客户端不会自动上传自己的文件。所有任务复用应用的媒体引擎、模型设置、隐私开关和任务队列。

使用官方 `ModelContextProtocol.AspNetCore 2.2.0`，协议版本为 [MCP 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)，传输为无状态 Streamable HTTP，端点为 `/mcp`。协议版本、请求头、工具发现、JSON-RPC 和结构化结果由官方 SDK 处理。

## 连接

### 网页工具箱

启用服务并保存设置后，打开 `http://127.0.0.1:18920/`；远程设备使用 `http://服务器IP:18920/`，并在宿主设置中开启「允许局域网连接」。桌面 MCP 设置页可直接「打开远程管理」或「复制网页地址」。页面随应用打包，不依赖前端开发服务器或 CDN。

1. 在 Avalonia Browser 界面的共享文件树中展开目录、选择文件夹；文件列表支持搜索、隐藏文件和多选。「添加所选项」或「添加当前文件夹」加入任务输入，可跨目录累计选择。
2. 「自动媒体整理」可选择分类规则、递归扫描、采样帧数、标签阈值及 NSFW/场景识别。默认只分析；完成后在详情预览分类目标，再点击「按此预览复制文件」创建整理任务，保留源文件。语义模型下载需勾选对应选项。
3. 工具由服务器能力列表提供，视频分类同时包含裁剪、旋转、多宫格截图。参数表单来自服务器能力定义：剪辑使用时间轴和起止点按钮，裁剪/去水印使用画面拖框，旋转/翻转有方向预览，其它参数使用滑块、开关、预设和文件树。网页不提供参数手填或高级 JSON。合并和混流通过上移/下移调整输入顺序，混流顺序为视频、音频。下载链接仍作为输入内容粘贴。可立即执行或仅加入队列。
4. 「所有任务」展示桌面与远程创建的任务，每 3 秒刷新状态，支持筛选、分页、开始、暂停、恢复、停止和重试。详情可查看结果、原始数据和分页日志，运行中可刷新详情。NSFW 分数、阈值、采样时间和后端单独展示；部分输出明确标注。输入与已结束任务的文件输出可在新标签页预览、下载，目录输出可返回文件浏览。
5. 「服务器」查看版本、平台、模型状态与资源策略，可修改 CPU 预留百分比、线程数和任务内部多线程开关。隐私和模型配置仍沿用宿主设置。

网页仅访问自身服务器的 `/mcp`。所选文件和输出路径均属于服务器，转换文件不经浏览器中转。提交前将 UUID 和完整参数保存在当前源的浏览器本地存储；响应丢失后使用「恢复上次提交」重发同一请求，刷新页面也保留恢复入口。服务器仅对仍保留的队列任务提供去重，不能在删除原任务后用恢复请求避免重复执行。放弃恢复前应先核对队列。浏览器须允许本地存储；窗口关闭不会取消已接收任务。

网页与现有 MCP 共用监听地址、服务开关和访问策略；后台宿主仍需要桌面会话。文件读取路由 `/web/tasks/{taskId}/file?source=input&index=0`（或 `source=output`）只从队列记录取路径，支持 HTTP Range；`download=true` 下载文件，非图片/音视频的格式强制下载，避免在服务源中执行 HTML 等文档。媒体数据仅在用户预览或下载时传输，转换和识别仍在服务器完成。浏览器验收入口见 [网页端到端验证](acceptance/WEB-CONSOLE.md)。

任务创建前，可通过 `avamedia_editor_preview` 从服务器读取定位帧和媒体信息。预览使用方形像素，拖框坐标按实际画面比例映射到旋转后的编辑像素网格，避开黑边并对齐偶数像素。`/web/media?path=...` 只播放已选的服务器音视频，支持 Range；沿用 MCP 对服务器绝对路径的访问权限，不接收可执行文档。浏览器不能解码的容器仍可由服务器生成预览帧和转换。播放器和媒体信息是读取操作，不创建空任务。

### MCP 客户端

支持 HTTP MCP 的客户端可采用以下配置；局域网连接时把地址换成设置页提供的网卡地址。

```json
{
  "mcpServers": {
    "AvaMedia": {
      "type": "http",
      "url": "http://127.0.0.1:18920/mcp"
    }
  }
}
```

先调用 `avamedia_capabilities` 查询当前功能、分类预设和任务控制动作。工具参数及结果的 JSON Schema 通过标准 `tools/list` 提供，包含 `ConversionOptions` 的嵌套参数及枚举。

Codex 可执行 `codex mcp add avamedia --url http://127.0.0.1:18920/mcp`。安装和使用技能位于 [avamedia-mcp](../.agents/skills/avamedia-mcp/SKILL.md)，发布包附带 `skills/avamedia-mcp` 整个目录；macOS 位于 `.app/Contents/MacOS/skills/avamedia-mcp`。技能包含客户端连接脚本、直接 HTTP 调用脚本和完整任务流程。配置依据 [OpenAI Docs](https://developers.openai.com/learn/docs-mcp)。

## 接口

| 工具 | 用途 |
| --- | --- |
| `avamedia_capabilities` | 当前服务能力、分类预设与任务控制动作 |
| `avamedia_file_locations` | 服务器上已有的输出目录、个人目录和磁盘位置 |
| `avamedia_browse_directory` | 单层目录导航，返回子目录、文件大小、修改时间、父目录及分页信息 |
| `avamedia_list_files` | 目录文件列表，可递归、筛选图片/视频、分页 |
| `avamedia_probe` | 时长、画面尺寸、编码及媒体轨信息 |
| `avamedia_create_task` | 创建现有服务的队列任务 |
| `avamedia_tag_media` | 图片、视频或整个文件夹的 AI 标签识别 |
| `avamedia_classify_media` | 按内置或自定义类别识别图片、视频、文件夹 |
| `avamedia_preview_rename` | 根据模板和 AI 关键词预览重命名 |
| `avamedia_apply_rename` | 把预览计划加入队列并执行重命名 |
| `avamedia_list_tasks` | 查询桌面和 MCP 创建的任务 |
| `avamedia_get_task` | 查询单项任务的进度与结果 |
| `avamedia_control_task` | 启动、暂停排队、恢复、停止、重试、移除 |
| `avamedia_get_runtime` | 当前宿主版本、后台状态、CPU 策略及隐私开关 |
| `avamedia_set_resources` | 更新 CPU 预留、线程及多线程开关 |
| `avamedia_list_models` | 模型目录、平台支持、安装和使用状态 |
| `avamedia_download_model` | 通过持久队列下载模型，复用断点与校验 |
| `avamedia_transcribe_media` | 后台识别字幕草稿，不编码原视频 |
| `avamedia_detect_people` | 后台人物片段检测，仅产出分析 |
| `avamedia_summarize_video` | 后台视频总结和结构化报告 |
| `avamedia_wait_task` | 最多等待 45 秒，返回当前进度或最终结果 |
| `avamedia_get_task_log` | 按字符分页读取持久日志 |
| `avamedia_export_task_result` | 将全部结果页导出到新的 JSON 文件 |
| `avamedia_preview_organization` | 预览分类目录和复制目标 |
| `avamedia_apply_organization` | 后台复制分类文件夹并保存操作日志 |

通用创建接口覆盖 `capabilities.features` 中标记 `canCreateTask=true` 的处理服务，包括视频/音频/图片转换、合并、裁剪、旋转、压缩、视频瘦身、下载、字幕、人物检测、视频总结、PDF、ZIP、光盘复制和多宫格截图。播放器、看图窗口使用桌面界面；媒体信息使用 `avamedia_probe`；重命名使用专用预览/执行接口。

分类接口生成分类结果及待确认项，源文件保持原位。`analysis` 可指定视频采样数、批次、场景识别和画面描述，`tagThreshold` 设置标签阈值。相似服装必须从预设选择 `byOutfit=true` 的规则；规则缺省仍使用默认分组。`preview_organization` / `apply_organization` 完成后台文件夹整理，默认排除待确认项并保留原文件。标签结果包含命中标签、分数、画面描述及模型错误；所需模型由现有服务管理。自定义语义分类需要语义模型，可通过 `allowSemanticDownload=true` 下载。未开启 NSFW 时，接口沿用应用的隐私过滤。

## 任务流程

远程文件选择先调用 `avamedia_file_locations`，再用返回路径调用 `avamedia_browse_directory`。浏览接口支持 `search`、扩展名数组 `extensions`、`foldersOnly`、`showHidden` 和 `offset/limit`；文件夹始终不受扩展名筛选影响。每页为当前目录的新视图，目录内容变化时应重新读取，而非把分页当作固定快照。列表中的路径属于当前服务器，切换服务器时清除先前选中文件。后续 `avamedia_create_task` 直接接收这些路径和服务器输出目录，媒体无需通过客户端中转。网页和桌面选择器的接入边界见[共享文件选择](FILE-SELECTION.md)。

创建接口使用客户端生成的 UUID `requestId`。相同 ID、相同参数返回仍保留在队列中的原任务；相同 ID、不同参数返回错误。任务移除后不保留此恢复记录。任务写入现有队列存储，HTTP 请求断开不取消已接收的任务。

创建时 `startImmediately=true` 立即启动，`false` 只加入队列。返回的是 AvaMedia 队列任务 ID；创建本身很快结束，随后使用 `avamedia_get_task` 查询，不依赖 MCP Tasks 扩展。任务在主界面显示 `MCP` 来源，进度、错误、结果与桌面操作共享。

状态为 `Waiting`、`Running`、`Completed`、`Failed`、`Cancelled`、`Paused`、`Stopping`。应用退出后，未完成任务按现有队列规则恢复为可重试状态。

`avamedia_control_task` 的 `action` 支持：

- `start`：启动等待任务。
- `pause`：暂停排队任务或在安全检查点暂停正在执行的任务。
- `resume`：恢复暂停排队的任务。
- `stop`：停止等待/暂停/执行中的任务。
- `retry`：重试失败或取消的任务；重命名需要重新预览并提交。
- `remove`：移除非执行中的任务及任务元数据，保留媒体输出文件。

列表与分类结果通过 `offset`、`limit` 分页，`limit` 为 1–200。分类结果在 `nextOffset` 有值时继续查询。二进制媒体通过输出路径返回，不嵌入 MCP 响应。

`get_task` 返回任务的部分结果标记、当前模型阶段和真实后端；分类文件还包含采样/推理帧数、标签及服装后端。`wait_task` 支持 `afterState` / `afterProgress`，没有变化时在超时后返回当前状态，HTTP 断开不取消任务。`get_task_log` 的 `offset` 是字符偏移，单页上限 65536 字符。

`export_task_result` 导出 Task、Partial 和全部 Pages，不覆盖现有文件；默认仅导出 Completed，停止/失败的结果需 `allowPartial=true`。资源接口只改变 CPU 预留（0–80%）、线程（1–16）及多线程开关，不改变隐私或模型选择。CPU 预留使用现有协作调度，不能保证峰值 CPU 的硬上限；多线程开关只作用于单任务线程，队列不限制并发任务数；已有原生会话可能在下次加载时采用新线程预算。

后台模型下载与分类复制仍使用共享 QueueService。复制计划在提交前重新预览并比较来源元数据和目标，执行时复用 FolderOrganization 的去重、内容校验和日志；部分失败/停止不会表示全部完成。失败后须重新预览再提交，不盲目重试已经生成的文件。

### 文件夹标签识别

调用 `avamedia_tag_media`：

```json
{
  "requestId": "c3c53d10-6979-496b-98e8-956a27801735",
  "paths": ["/Users/example/Videos"],
  "recursive": true,
  "startImmediately": true
}
```

返回每个媒体文件的任务 ID。对每个 ID 调用 `avamedia_get_task`，设置 `includeResult=true`，读取标签和画面描述。

### 视频分类

调用 `avamedia_classify_media`，传入视频或文件夹路径、可选分类 `rules` 和新的 `requestId`。`rules` 的结构可从 `avamedia_capabilities.classificationPresets` 获取，也可自定义分类名称、类别描述、标签与阈值。每个分类任务包含整批输入，并可在桌面打开分类结果。

### AI 重命名

AI 先读取标签结果，选出关键词，然后调用 `avamedia_preview_rename`：

```json
{
  "paths": ["/Users/example/Videos/001.mp4"],
  "rules": { "pattern": "{keyword}_{index}", "digits": 3 },
  "keywords": { "/Users/example/Videos/001.mp4": "城市夜景" }
}
```

将返回的完整 `plan` 和新的 `requestId` 原样传给 `avamedia_apply_rename`。服务在执行前重新检查文件大小、修改时间、重复来源、目标冲突和文件名，使用现有两阶段事务及重命名日志。执行事务开始后，停止请求不打断改名提交/回滚；已提交的改名保持完成状态。

### 普通媒体任务

调用 `avamedia_create_task`，例如：

```json
{
  "requestId": "ae63d651-c4d7-47e3-9996-cbdbb4ff7af1",
  "featureId": "audio-mp3",
  "paths": ["/Users/example/Videos/example.mp4"],
  "outputFolder": "/Users/example/Exports",
  "startImmediately": true
}
```

省略 `options` 或 `options.format` 使用该服务的默认格式；显式格式会被保留。其它参数遵循 `tools/list` 的选项 Schema 和服务参数校验。合并、混流等按传入的文件顺序处理。下载输入为 URL。输出名称沿用现有去重机制。人物检测根据导出预设决定容器和流复制选项，多宫格默认输出单张 PNG。

## 实现与检查范围

`AvaMedia.Mcp` 提供协议宿主、工具和与 UI 无关的工作区接口；桌面适配器在 Avalonia Dispatcher 上读写任务集合，目录扫描在后台进行。已有服务直接通过共享队列执行；批量重命名和多宫格截图补充队列执行入口。

`tests/AvaMedia.McpTests` 使用官方 C# MCP 客户端、原生 Avalonia 主窗口和真实 `MediaEngine`。原有验收覆盖当时的 11 个工具发现、无令牌访问、目录分页与空目录、参数错误、主界面任务及 MCP 来源标记、暂停/继续/停止/移除、失败重试、重复请求、默认与显式格式、真实图片/视频标签推理、分类结果分页、标签驱动的事务重命名、变更源文件拒绝、MP3/多宫格实际输出及解码、重启后的任务和 AI 结果恢复、CORS 及监听服务重配置。源样本通过 SHA256 检查保持不变。本次扩展至 23 个工具，无窗口入口及新增接口按工作约定仅检查相关编译和脚本/技能静态诊断；历史验收不代表新增功能已完成运行验收。

显式运行验收（需要桌面会话、FFmpeg/FFprobe，以及已安装的 JoyTag 模型）：

```sh
dotnet run --project tests/AvaMedia.McpTests -c Release -- artifacts/mcp-acceptance /absolute/path/to/portrait.jpg
```

输出目录须不存在；程序会复制样本、生成 2 秒视频，使用独立设置和队列目录，保存 `report.json`、工具 Schema 和逐次 MCP 响应。2026-10-10 在 macOS 上完成 15 项验收、165 次 MCP 调用，零失败；干净源码快照编译零警告、零错误。局域网模式验证了本机监听切换，未使用第二台设备验证跨机访问，也未覆盖所有转换服务或其它操作系统。

## 构建网页界面

```sh
.tools/dotnet/dotnet workload install wasm-tools --skip-manifest-update
.tools/dotnet/dotnet publish src/AvaMedia.Browser/AvaMedia.Browser.csproj -c Release
.tools/dotnet/dotnet build src/AvaMedia.Desktop/AvaMedia.Desktop.csproj -c Release
```

先发布 Browser，再构建 Desktop，才能把生成的 `wwwroot` 复制到客户端输出目录。`scripts/Publish.ps1` 和 CI 已包含这一顺序；Release 的 Windows/macOS job 安装 WebAssembly 工具。没有网页产物的开发构建在根地址返回 503 并说明构建步骤，MCP 端点照常可用。网页复用仓库已有 Fusion Pixel 中文字体，不依赖外部字体服务。
