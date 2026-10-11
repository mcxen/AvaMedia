# 共享文件选择与导入

工具的文件入口统一经过 `AvaMedia.Desktop.ToolFilePicker`。打开文件和选择文件夹默认使用应用内浏览窗口；保存文件经过同一服务调用系统保存窗口，保留文件名、覆盖确认和写入授权行为。浏览窗口中的“系统选择…”用于访问系统文件提供者或取得系统授权。

## 共享组件

`src/AvaMedia.FileUi` 仅依赖 Avalonia，不引用 Desktop、Core、FFmpeg 或本地 AI 库。

- `FileBrowserView`：可嵌入的浏览界面。提供常用位置、地址输入、后退、上一级、刷新、当前目录搜索、隐藏文件开关、类型筛选、单选和多选。Ctrl/Cmd+L 定位地址栏，Alt+↑ 进入上一级，Enter 打开所选文件夹或确认文件，Esc 取消。
- `IFileBrowserSource`：提供位置、地址解析和可取消的目录读取。结果保留 `IStorageItem`、`IStorageFile`、`IStorageFolder` 对象，组件不把它们转换成本地路径。
- `FileImportActions`：共享添加文件、添加文件夹入口，统一处理异步导入、重复点击和错误回报。工具可追加移除、排序、选择等操作。
- `FileImportPanel`：将共享操作区与工具现有文件列表组合，也可接收拖入的存储对象。文件预览、文件顺序、裁剪区域和字幕等业务状态由工具继续管理。

Desktop 的 `DesktopFileBrowserSource` 负责本地目录和磁盘位置，在后台读取目录并检查取消。窗口关闭或切换目录后，旧的目录读取结果不会覆盖新状态。文件名和用户输入不参与界面文本翻译。

文件浏览入口覆盖所有工具及辅助文件入口，包括字幕、词库、模型、命名规则和 cookies 文件。导入操作组件用于转换、混流、视频压缩/瘦身/裁剪/旋转、图片压缩、PDF、AI 标签、分类、重命名、人物片段、快速剪辑、多宫格截图、媒体信息、文件路由、语音和视频总结。单个参数的浏览按钮继续保持紧凑的字段布局。

## 网页端接入边界

后续 Avalonia Browser 工具箱可以引用 `AvaMedia.FileUi`，嵌入 `FileBrowserView` 或 `FileImportPanel`。网页宿主需要提供 `IFileBrowserSource`，从用户授权的目录句柄或已上传的素材空间取得位置和文件，接收选择结果后使用 `IStorageFile.OpenReadAsync()` 读取。桌面工具目前仍在自己的业务边界使用本地路径，不代表媒体处理引擎已经可在浏览器运行。

浏览器访问用户文件和目录需要用户授权，不能假定可以枚举整块磁盘，也不能把桌面绝对路径当成网页文件标识。若网页使用 HTML 前端，可以复用同一选择/导入行为和素材接口，但需实现对应的 Web 视图；Avalonia 控件不是 HTML 组件。参考 [Avalonia Storage Provider](https://docs.avaloniaui.net/docs/services/storage/storage-provider) 和 [File System API](https://developer.mozilla.org/en-US/docs/Web/API/File_System_API)。

本次检查范围是 FileUi、Desktop 及其依赖的 Release 编译和相关静态检查。未运行界面、媒体输出或浏览器功能验收。
