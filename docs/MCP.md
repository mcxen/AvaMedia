# MCP 服务

在「选项 → MCP」开启服务并应用，复制地址或 MCP 配置即可连接。默认端口为 `18920`，本机地址为 `http://127.0.0.1:18920/mcp`。开启「允许局域网连接」后，设置页列出当前网卡地址，其他设备使用其中一个地址连接。服务随主窗口启动，缩到托盘时继续运行，正常退出时停止。

按当前产品要求，服务无需访问令牌，不限制文件目录，开放跨域访问。文件路径指运行 AvaMedia 的机器上的绝对路径；远程客户端不会自动上传自己的文件。所有任务复用应用的媒体引擎、模型设置、隐私开关和任务队列。

使用官方 `ModelContextProtocol.AspNetCore 2.2.0`，协议版本为 [MCP 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)，传输为无状态 Streamable HTTP，端点为 `/mcp`。协议版本、请求头、工具发现、JSON-RPC 和结构化结果由官方 SDK 处理。

## 连接

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

## 接口

| 工具 | 用途 |
| --- | --- |
| `avamedia_capabilities` | 当前服务能力、分类预设与任务控制动作 |
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

通用创建接口覆盖 `capabilities.features` 中标记 `canCreateTask=true` 的处理服务，包括视频/音频/图片转换、合并、裁剪、旋转、压缩、视频瘦身、下载、字幕、人物检测、视频总结、PDF、ZIP、光盘复制和多宫格截图。播放器、看图窗口使用桌面界面；媒体信息使用 `avamedia_probe`；重命名使用专用预览/执行接口。

分类接口生成分类结果及待确认项，源文件保持原位；可在桌面的分类任务窗口继续整理。标签结果包含命中标签、分数、画面描述及模型错误。`analysis` 可指定视频采样数、场景识别和画面描述；所需模型由现有模型服务管理。自定义语义分类需要语义模型，可通过 `allowSemanticDownload=true` 下载。未开启 NSFW 时，接口沿用应用的隐私过滤。

## 任务流程

创建接口使用客户端生成的 UUID `requestId`。相同 ID、相同参数返回仍保留在队列中的原任务；相同 ID、不同参数返回错误。任务移除后不保留此恢复记录。任务写入现有队列存储，HTTP 请求断开不取消已接收的任务。

创建时 `startImmediately=true` 立即启动，`false` 只加入队列。返回的是 AvaMedia 队列任务 ID；创建本身很快结束，随后使用 `avamedia_get_task` 查询，不依赖 MCP Tasks 扩展。任务在主界面显示 `MCP` 来源，进度、错误、结果与桌面操作共享。

状态为 `Waiting`、`Running`、`Completed`、`Failed`、`Cancelled`、`Paused`、`Stopping`。应用退出后，未完成任务按现有队列规则恢复为可重试状态。

`avamedia_control_task` 的 `action` 支持：

- `start`：启动等待任务。
- `pause`：暂停尚未执行的排队任务。
- `resume`：恢复暂停排队的任务。
- `stop`：停止等待/暂停/执行中的任务。
- `retry`：重试失败或取消的任务；重命名需要重新预览并提交。
- `remove`：移除非执行中的任务及任务元数据，保留媒体输出文件。

列表与分类结果通过 `offset`、`limit` 分页，`limit` 为 1–200。分类结果在 `nextOffset` 有值时继续查询。二进制媒体通过输出路径返回，不嵌入 MCP 响应。

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

省略 `options` 使用该服务的默认格式；显式传入时遵循 `tools/list` 的选项 Schema 和服务参数校验。合并、混流等按传入的文件顺序处理。下载输入为 URL。输出名称沿用现有去重机制。人物检测根据导出预设决定容器和流复制选项，多宫格默认输出单张 PNG。

## 实现与检查范围

`AvaMedia.Mcp` 提供协议宿主、工具和与 UI 无关的工作区接口；桌面适配器在 Avalonia Dispatcher 上读写任务集合，目录扫描在后台进行。已有服务直接通过共享队列执行；批量重命名和多宫格截图补充队列执行入口。

本次本地检查限定为受影响的 Desktop/Core/MCP 编译及修改的静态检查。未运行 MCP 客户端连接、局域网、真实媒体输出或界面功能验收。
