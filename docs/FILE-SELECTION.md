# 共享文件选择与导入

工具的文件入口统一经过 `AvaMedia.Desktop.ToolFilePicker`。打开文件和选择文件夹默认使用应用内浏览窗口；保存文件经过同一服务调用系统保存窗口，保留文件名、覆盖确认和写入授权行为。浏览窗口中的“系统选择…”用于访问系统文件提供者或取得系统授权。

桌面普通选择器按 Git 初版 `1156f5e` 的列表流程恢复：左侧平铺常用目录与已有收藏，右侧仅名称、大小和修改时间；完整地址可直接输入。单击选择，双击进入文件夹 / 打开文件，Ctrl/Cmd 与 Shift 多选。默认窗口 880 × 600，不加载媒体缩略图或检查器，不自动跳到最近使用；最近使用仍可由菜单单独打开。

## 共享组件

`src/AvaMedia.FileUi` 仅依赖 Avalonia，不引用 Desktop、Core、FFmpeg 或本地 AI 库。

- `FileBrowserView`：可嵌入的浏览界面。桌面选择 `Simple` 列表呈现，嵌入端按其场景选择媒体或紧凑呈现；共用来源、过滤、可取消的导航和文件确认。提供地址输入、后退、上一级、刷新、隐藏文件开关、文件类型、单选和多选。Ctrl/Cmd+L 定位地址栏，Alt+↑ 进入上一级，Enter 打开所选文件夹或确认文件，Esc 取消。
- `PathInput`：浏览地址默认显示末两级名称，超长部分省略；悬停查看完整路径，点击编辑完整地址。编辑时 Esc 恢复原值，Enter 确认。目录导航失败后恢复当前目录的地址，避免地址与列表不一致。网页任务的输出目录、分类复制目标和字幕等参数通过独立文件树选择，采用只读紧凑路径显示，不要求手填路径。
- `IFileBrowserSource`：提供位置、地址解析和可取消的目录读取。使用 `FileBrowserItem` 保存来源地址、名称和目录标记；桌面适配器同时保留原始 `IStorageItem`，远程适配器保留服务器路径，不实现 Avalonia 私有存储接口。
- `FileImportActions`：共享添加文件、添加文件夹入口，统一处理异步导入、重复点击和错误回报。工具可追加移除、排序、选择等操作。
- `FileImportPanel`：将共享操作区与工具现有文件列表组合，也可接收拖入的存储对象。文件预览、文件顺序、裁剪区域和字幕等业务状态由工具继续管理。

Desktop 的 `DesktopFileBrowserSource` 负责本地目录和磁盘位置，在后台读取目录并检查取消。窗口关闭或切换目录后，旧的目录读取结果不会覆盖新状态。文件名和用户输入不参与界面文本翻译。

文件浏览入口覆盖所有工具及辅助文件入口，包括字幕、词库、模型、命名规则和 cookies 文件。导入操作组件用于转换、混流、视频压缩/瘦身/裁剪/旋转、图片压缩、PDF、AI 标签、分类、重命名、人物片段、快速剪辑、多宫格截图、媒体信息、文件路由、语音和视频总结。单个参数的浏览按钮继续保持紧凑的字段布局。

## 网页端接入边界

网页工具箱的主要流程是远程控制：连接 AvaMedia 服务器，浏览并选择服务器上的文件，选择服务器提供的工具及参数，下发任务，查看进度与输出位置。文件读取、媒体转换和模型推理均由服务器完成；已有远程文件无需下载到浏览器再上传。

```mermaid
flowchart LR
    Web[网页文件选择与工具参数] -->|服务器文件标识与任务参数| Service[AvaMedia 服务接口]
    Service --> Queue[共享持久任务队列]
    Queue --> Engine[服务器媒体引擎与模型]
    Engine --> Output[服务器输出文件]
    Queue -->|任务状态与结果| Web
```

Avalonia Browser 工具箱已引用 `AvaMedia.FileUi`，嵌入 `FileBrowserView` 和共享 `FileTreeView`，由远程 `IFileBrowserSource` 分页读取服务器目录元数据。展开树节点只查询直接子目录，文件列表支持多选文件和文件夹。

远程文件与下发任务遵循以下约定：

- 所选文件绑定服务器标识与服务器路径。网页不使用客户端操作系统规则改写服务器路径；切换服务器时重置当前目录和选中文件，避免把同名路径提交到另一台机器。
- 输入目录、输出目录均属于当前服务器。`avamedia_file_locations` 返回服务器位置，`avamedia_browse_directory` 返回子目录、文件名、类型、大小、修改时间、父目录和分页信息。`avamedia_list_files` 继续用于收集可处理文件路径。
- 工具列表从 `avamedia_capabilities` 读取，模型及资源状态从 `avamedia_list_models`、`avamedia_get_runtime` 读取，沿用服务器实际能力与设置。
- 转换复用 `avamedia_create_task`；请求使用 UUID `requestId`，丢失响应后以相同 ID 和参数恢复任务。任务与桌面共用队列，不因关闭网页或断开 HTTP 请求而取消。
- 进度、错误和结果复用 `avamedia_list_tasks`、`avamedia_get_task`、`avamedia_wait_task` 和日志接口；暂停、恢复、停止和重试沿用 `avamedia_control_task`。网页详情通过队列记录关联的文件路由预览或下载输入/输出，转换过程仍直接使用服务器路径。

`src/AvaMedia.Browser` 使用 Avalonia 11.3.22 / .NET 8 WebAssembly；发布后的 `wwwroot` 随桌面客户端打包，HTTP 服务从同一端口提供界面和任务接口。客户端用 `--server` 可常驻后台提供远程管理。宿主仍需要桌面会话，尚非无图形会话的独立系统服务。具体操作见 [MCP 与远程管理](MCP.md)。

本地文件上传属于另一种可选来源。若以后接入，应从用户授权的目录句柄或文件对象读取，通过上传得到服务器文件标识后再下发任务；不与服务器文件浏览混用。参考 [Avalonia Storage Provider](https://docs.avaloniaui.net/docs/services/storage/storage-provider) 和 [File System API](https://developer.mozilla.org/en-US/docs/Web/API/File_System_API)。

共享控件最初完成编译和静态检查；网页后续按用户要求增加真实浏览器、文件和媒体任务验收，范围及结果见 [网页端到端验证](acceptance/WEB-CONSOLE.md)。
