# AvaMedia 界面规范 · macOS 26 浅色

本文件是 AvaMedia 桌面端、编辑器和远程网页管理的统一视觉与交互规范（设计代号 “AvaMedia macOS 26 light”）。
参考图在 [docs/ui/](docs/ui/)。任何界面改动前先读本文件和参考图；新界面必须能在这里找到依据，找不到时先补规范再写代码。

## 1. 设计令牌

| 令牌 | 值 | 用途 |
| --- | --- | --- |
| 页面背景 | `#F2F3F7` | 窗口与网页底色 |
| 侧栏底色 | `#F1F4FA` | 桌面左侧导航、工具树 |
| 卡片 / 表面 | `#FFFFFF` | 卡片、列表、检查器 |
| 填充 | `#F0F0F3` | 次级按钮、分段控件底、片段列表底 |
| 分隔线 | `#E5E5EA` 1 px | 列表行之间的发丝线 |
| 主文字 | `#1D1D1F` | 标题、正文 |
| 次文字 | `#86868B` | 说明、时间、元数据 |
| 系统蓝 | `#007AFF` | 主按钮、选中、链接、进度 |
| 浅蓝 | `#EAF3FF` | 选中行、工具徽标底 |
| 成功 | `#34C759`（文字 `#248A3D`） | 已完成、开关开启、在线 |
| 暂停 / 警告 | `#FF9500` | 已暂停、提醒横幅图标 |
| 失败 / 危险 | `#FF3B30` | 失败、删除 |
| 等待 | `#86868B` | 等待、已取消 |
| 横幅 | 底 `#FFF8E6`，文字 `#8A5A00` | 恢复提示 |

状态色只用于状态图标、状态文字和进度条，不用来填满整行。

- 分类界面颜色通过 `ClassifyUi.Resource` 动态绑定到所属控件的资源；`UiFill`、`UiClassificationPage`、`UiAccentSoft`、`UiAccentDescription`、`UiSuccessFill`、`UiBanner`、`UiBannerText`、`UiTrack`、`UiClassificationNote` 在浅色、深色、Platinum、XP 中分别定义。媒体采样得到的真实颜色仍直接显示。
- 分类主按钮使用 `UiClassificationPrimary` / `UiClassificationPrimaryText`，规则卡片选中态使用 `UiClassificationRuleFace` / `UiClassificationRuleText`；经典皮肤采用原生浅色按钮底与深色文字。`UiClassificationPillBorder` 保留原生按钮边框；`UiCardShadow` / `UiSegmentShadow` / `UiFocusShadow` / `UiThumbnailShadow` 在经典皮肤中为空。现代提交模板排除 Mac OS 9 / XP。
- 网页 `RemoteTheme.axaml` 中样式、模板和阴影均引用主题资源；成功文字使用 `#248A3D`、横幅使用 `#FFF8E6`、等待文字使用 `#86868B`。

## 2. 字体、间距、圆角、行高

- 字体：桌面跟随系统（macOS 为 SF Pro / 苹方）；网页使用内置 Noto Sans SC 子集，像素字体只作回退。
- 字号：大标题 26 粗；页面标题 17 粗；卡片标题 15 粗；正文 13；说明 11.5–12；等宽时间码 12。
- 间距：基础单位 4；卡片内边距 14；卡片间距 12；页面外边距桌面 18、手机 16。
- 圆角：卡片 14；按钮为胶囊（高度的一半）；输入框、下拉 7；缩略图 6–9；分段控件 9（内段 7）。
- 行高：任务行 ≥ 56；文件行 29；设置行 42–44；片段 / 区域行 32。
- 阴影：卡片 `0 1 3 #14000000`；悬浮按钮 `0 6 16 #55007AFF`；不叠加多层阴影。

## 3. 控件

- **胶囊按钮**：默认浅灰填充、深色文字，可带 14 px Lucide 图标；`primary` 为系统蓝底白字，每个区域最多一个；`danger` 为红字；`link` 无底色蓝字。
- **主按钮**：底部操作栏右侧，如“创建任务”。大号（高 40）只用于底部操作栏与手机悬浮按钮。
- **图标按钮**：只有图标时必须有 ToolTip 和无障碍名称。
- **分段控件**：浅灰底、选中段白底加细阴影；用于“所有任务 / 文件与任务下发 / 服务器”、“结果 / 处理日志”和编辑器模式切换。
- **筛选胶囊**：文字 + 服务器计数（例如“等待 4”），选中为蓝底白字。计数来自服务器，不在本地估算。
- **发丝线列表**：白底，行与行之间 1 px `#E5E5EA`，左侧缩进与内容对齐；不画外框和斑马纹。
- **复选框 / 开关**：复选框蓝色圆角方块；开关为绿色 iOS 样式。
- **状态图标**（Lucide）：运行中 `loader-circle` 蓝；等待 `clock` 灰；已暂停 `pause` 橙；已完成 `check` 绿；失败 `circle-alert` 红；已取消 `ban` 灰。图标后跟状态文字和百分比。
- **图标**：统一使用 Lucide 线性图标（描边 2），不用 emoji、不混用其他图标库。

## 4. 工具树与任务列表

- **工具树**：按“视频 / 音频 / 图片 / 文档 / 工具集 / 光盘”分类的手风琴，同时只展开一个分类；分类行显示图标、名称和数量；展开后按小节列出工具“胶囊”，选中为蓝底白字。不提供搜索框。
- **任务列表**：每行包含名称（粗体）、“工具 · 来源”、状态列（状态图标 + 文字 + 进度条 + 当前活动）和右对齐的操作按钮。列宽固定，状态列和操作列逐行对齐。
- 每种状态只显示该状态可用的操作：运行中“暂停 / 停止”，等待“开始 / 暂停 / 停止”，已暂停“恢复 / 停止”，失败“重试”，全部带“详情”。
- 选中行浅蓝底并在左侧显示 3 px 蓝色强调条；详情在右侧面板（手机为整页），包含缩略图、状态、操作、“结果 / 处理日志”分段、键值列表和日志框。
- 列表每 3 秒刷新一次，刷新时保持行顺序与选中项，不整表重建；底部分页“共 N 项 · 第 n 页”。
- 任务区只放任务，不放工具入口。

## 5. 桌面窗口

- 使用平台窗口外观：macOS 红绿灯在左，Windows / Linux 按钮在右，统一通过 `MainWindow.WindowChrome` 复用，不另写标题栏。
- 主窗口为“侧栏 + 内容”两栏：侧栏 `#F1F4FA`，内容区白色卡片；设置窗口按分组卡片排列，“远程管理与 MCP”只显示真实数据（地址、状态、最近客户端）。
- 浅色皮肤为主要设计目标；其他皮肤（深色、Platinum 等）必须保持可用，不能出现不可读文字或错位。

### AI 媒体整理提交窗口

- 紧凑窗口约 900 × 520，底色 `#FAFAFC`；两栏宽度比 1 : 1.75，栏间距 18；标题 15 粗，底部次级按钮和主按钮高 32。
- 分类规则为四列等宽卡片，高 46、圆角 10、间距 8；标题 12.5 半粗。选中为系统蓝底、白色勾选及标题、浅蓝说明；未选中为白底与 1 px 分隔线色边框。悬停、按下保持卡片色彩；更多规则在卡片区域滚动。
- 来源使用真实媒体组成 128 × 96 的 2 × 2 拼图，外圆角 10；下方为粗体名称与次色数量。输出目录卡片白底、圆角 10，蓝色文件夹图标及更改胶囊。
- 参数卡片标签 11 次色、数值 15 粗；轨道高 4，系统蓝填充。帧数用与当前帧数相同数量的短划线表示；保留滑条点击、拖动和键盘调整，悬停或键盘聚焦时显示小滑块。
- 复选框为 14 px 蓝色圆角方块；复制 / 移动分段使用灰底、白色选中段与轻阴影。

### 自动字幕提交与 AI 进度 v2

- 桌面提交窗口约 940 × 560，左侧文件列表与单张预览，右侧识别设置和四步运行说明；设置默认尺寸下完整显示，底栏为取消、加入队列、后台识别。选择项保留蓝底白字，主按钮使用系统蓝。
- 桌面字幕进度约 1060 × 712：顶部任务、模型后端、百分比和操作；四张阶段卡片依次为读取音轨、语音模型、语音识别、保存结果。下方为分段轨、波形、最新字幕，右侧过程记录与完成后操作。
- 阶段卡片高 160、圆角 12；运行阶段蓝色细边框，完成阶段绿色序号。仅对真实进度与已检测语音分段着色，未运行阶段保持灰色。
- v2 尺寸对照以桌面窗口客户区为基准，标题区高 50；提交正文距顶 54，文件卡片按内容定高（3 行约 180），文件行高 56、内边距 8，预览固定高 160，禁止用剩余高度拉伸文件卡片。设置字段上下内边距 10，选择胶囊高 28，模型卡片约 50，运行说明卡片上下内边距 12、左右 16。
- 字幕进度阶段框至正文间距 9；正文卡片上下内边距 14、左右 16，右侧卡片上下 12、左右 14，“完成后”卡片至少高 142；字幕行高至少 34、上下内边距 7，过程记录行上下 3，列表容器不再叠加默认内边距。长文本按需增高；长列表在既定视口内滚动。
- 媒体整理进度阶段框至少高 75、内边距 12 / 10；当前文件卡片上下 12、左右 14，标题与步骤间距 10，不为活动文案额外保留一行（放入标题提示）。分组列表缩略图 52、行上下内边距 6。当前画面中的帧条高 38、帧间距 4，文件流缩略图高 44。
- 媒体整理进度的候选帧按实际采样顺序单行显示，当前帧为 2 px 系统蓝框；标题显示真实帧序号。服装主色按检测比例分段，悬停显示百分比，下方为浅蓝归组理由。文件流固定单行 8 张，显示当前文件前后的处理顺序，完成为绿色勾、当前为蓝框、排队透明度 40%。分组封面使用左大右侧叠放两张的拼图，蓝色 +N 角标带白边，信息为分组名称、拍摄组及相似度 / 场景、17 px 粗体数量。预览类别标签为白色胶囊、蓝色置信度，场景标签置于左下角，深色半透明底、白字及浅蓝置信度；标签位置按预览宽高比例定位。标题百分比为 26 px 粗体，临时停止为普通胶囊，结束任务为红色 1 px 描边胶囊。
- 网页字幕卡片左右外边距 24、间距 14，卡片上下内边距 18、左右 20；设置字段上下 10，常规单行字段至少高 40，立即执行紧随设置字段，提交按钮位于卡片底部。结果与日志卡片上下内边距 12、左右 14。
- 手机摘要卡片约高 155，卡片间距 10；结果卡片高约 299（字幕列表视口 252），日志卡片上下内边距 12、左右 14，摘要与日志随实际内容增高；字幕行至少高 51、上下内边距 7。底栏高 96、按钮高 44，导航不补画设备状态栏。
- 网页提交为服务器文件和识别设置两栏，宽度比约 1.9 : 1，主按钮置于设置卡片底部。进度页左侧任务导航宽 280，右侧显示任务缩略图、四步进度、当前活动、字幕结果和最近日志。
- 手机字幕详情隐藏远程管理顶栏及底部标签栏，显示返回所有任务、居中任务详情；内容为摘要、字幕结果、日志卡片，暂停和停止固定在底栏。系统状态栏由设备提供，不在网页中模拟。
- 字幕列表保留虚拟化和跟随最新字幕操作；长列表可滚动查看历史，空数据不生成字幕行。截图夹具只用于对照，不进入产品界面。

## 6. 编辑器布局

参考 `docs/ui/editor-demo.png`、`editor-crop.png`、`editor-demo-mac.png`。

- 顶部：平台窗口外观 + 居中分段模式切换；快速剪辑仅有“剪辑 / 画面裁剪 / 自动转正”，经典编辑器保留“选项”。
- 中部左侧：大预览（黑底圆角 10）+ 传输控制（跳到起点、上一帧、播放、下一帧、跳到终点、时间码 `mm:ss.mmm / 总时长`）。
- 预览下方：胶片条时间线（缩略图由共享胶片条服务生成，可缩放），片段为蓝框，选中片段显示两端拖动柄；拖动柄时浮出该时间点缩略图和“起点 / 终点 时间码”。
- 右侧检查器：片段列表（小缩略图、区间、时长）、选中片段的“起点 / 终点”首尾帧卡片（时间码、前后一帧微调、跳转），以及按卡片分组的参数；快捷键仅放在 tooltip 中，不显示键帽或提示行。
- 快速剪辑：侧栏宽 330，片段行高 40，首尾帧高 75；时间字段为居中的 11.5 半粗 `mm:ss.fff`（超过一小时为 `h:mm:ss.fff`）。属性行高 34，值用 `#6E6E73`，无框下拉加细灰箭头。时间线工具按钮高 30，标尺高 14，胶片条高 52，底部仅显示 11 px 次文字摘要；缩放轨宽 70、蓝色填充、白色阴影滑块。底栏提供输出配置、下一个视频和进入导出选项；进入导出选项前，其他勾选的视频必须已有剪辑，否则保留编辑器与草稿，弹出“还有视频未剪辑”模态提示，逐行列出文件名，说明需先剪辑或取消勾选。提示沿用平台窗口外观、18 px 内边距和 12 px 间距，文件列表可滚动，底部右侧仅一个“好”主按钮。Platinum 保留原生浮雕按钮及清晰文字。
- 底部导出栏：左侧摘要，右侧主按钮。
- 所有已有功能和快捷键必须保留。
- 快速剪辑三种模式始终显示公共标题栏、预览、传输控制、胶片条时间线与底栏。画面裁剪使用右侧检查器放置比例、精确坐标、重置与去水印选区；自动转正卡片追加在时间线下方，预览可适度缩小，卡片圆角 14、内边距上 12 / 左右与下 14，高约 148，保留方向胶囊、镜像、识别状态与操作。1280 × 800 下时间线不得被模式卡片替代。
- 标题栏文件名 / 副标题之后放置带提示与无障碍名称的小型媒体信息图标；悬停和点击均打开卡片式浮层，显示文件名、时长、分辨率、视频与音频编码。信息行高 32，标签灰色、值右对齐且数字等宽；长文件名省略并提供完整悬停提示。深色使用 Qc 令牌，Platinum 保留原生控件外观。
- 不再显示快速剪辑选项页卡片。“输出配置…”打开完整片段设置弹窗，预览摘要区显示实时音频状态与输出说明；音量、静音、音频效果和字幕等仍在此处。旋转 / 镜像在自动转正可达，速度 / 视频淡入淡出在剪辑侧栏可达，裁剪与去水印选区在画面裁剪可达，均读写同一片段选项。

## 人物检测任务详情（B 版）

- 依据 `/workspace/AvaMedia-sidebar-review/person-task/person-task.html?v=b`、浅深色图及 `?v=s` 状态图：左栏占内容宽度约 42%，展示真实首帧或所选片段代表帧、文件名、分辨率 / 编码 / 时长与唯一状态胶囊；下方为保留片段、保留时长、用时、检测帧数的 2×2 数字卡。数字和时间码使用等宽数字。缺帧时只显示中性“暂无预览”空状态，投影复用 10.x 的公共预览容器。
- 右栏上方为全长时间轴、图例和可横向滚动的片段缩略图条，选中片段以强调色描边；时间轴悬停提示放在条下方，不能遮挡图例。下面是可滚动片段列表，列表末尾的处理记录默认折叠一行，展开查看完整日志。
- 进行中使用真实进度、采样帧数和部分结果增量填充时间轴；未检测部分不能视为已移除。失败显示红色原因并保留中断处及部分结果，不显示调整片段按钮。停止、暂停及等待保留真实队列操作。
- 底部左侧新建任务 / 重新执行，右侧关闭 / 唯一主按钮调整片段，进行中右侧提供停止。全部计数与片段来自任务结果；没有结果显示“—”或简短空状态。缩略图仅加载可见项，后台解码、可取消、复用媒体缓存，不阻塞 UI。皮肤颜色与圆角复用 Ui / AvaMedia.Controls 主题令牌。

## 7. 远程网页

参考 `docs/ui/remote-*.png`。

- **桌面（≥ 760 px）**：顶部为 logo、标题、连接状态胶囊、居中分段导航（所有任务 / 文件与任务下发 / 服务器）、“重新连接”。默认打开“所有任务”。
- **新建任务**：三张编号卡片“1 选择文件 · 2 选择工具 · 3 输出与提交”，宽度约 1.35 : 1 : 1；底部操作栏放“包括子文件夹 / 立即执行”和主按钮；未确认的上次提交用顶部横幅提示“放弃 / 恢复”。
- **文件浏览**：返回 / 上一级 / 面包屑 / 刷新；“常用位置”只列有意义的位置（不显示 /proc、/sys、/dev 等）；行首复选框，点文件夹进入。
- **编辑器**：顶部为返回、文件胶囊、工具徽标；左侧舞台（预览、传输、时间线或工具专属底栏），右侧检查器。剪辑、裁剪、去水印、合并都调用真实接口（`/web/preview/frame|strip|boundaries|nudge`）。
  - 剪辑：多片段、首尾帧卡片、逐帧微调、拖动柄缩略图浮层。
  - 裁剪：比例胶囊（自由 / 16:9 / 9:16 / 4:3 / 3:4 / 1:1）、三分网格、输出尺寸。
  - 去水印：多区域（最多 16 个），每个区域可设出现时间；非当前区域以标签框显示。
  - 合并：缩略图行、拖动手柄排序并保留上下移按钮、按时长比例的色条。
- **手机（< 760 px）**：大标题、底部标签栏（所有任务 / 任务下发 / 服务器）、卡片式任务、右下悬浮“创建任务”；新建任务为分步导航，底部固定“下一步”主按钮；编辑器上下排列。
- 网页文案中英文双语：中文原文作为键，英文在 `src/AvaMedia.Browser/Strings/Remote.json` 和桌面词典中；文件名、路径等用户数据标记为 raw，不翻译。

## 8. 禁止事项

- 不提供搜索框。
- 不使用虚线拖放区和“拖到这里”提示框。
- 界面任何位置均不使用 emoji。
- 任务区不放工具入口；工具只在工具树 / 侧栏中出现。
- 不在一屏中放多个蓝色主按钮；不用状态色填满整行。
- 不显示占位或假数据；没有数据时显示简短空状态文字。
- 不在界面上直接显示快捷键（按钮上的键帽如 [S]/[I]/[O]/Ctrl D、底部快捷键提示行等都不要）；快捷键只写在悬停提示（tooltip）里，快捷键本身照常可用。

### 10.x 媒体预览的投影与氛围光（视频处理工具通用）

参考代码：

- 公共容器：`src/AvaMedia.Desktop/Controls/MediaPreviewSurface.cs`。投影在 Viewbox 外绘制，保持显示像素半径；氛围光半径 72、扩散 12、颜色过渡 400ms。没有真实帧立即隐藏光晕；经典皮肤跳过后台取色。
- 主题令牌：`src/AvaMedia.Desktop/Styles/MediaPreviewStyles.axaml`。`UiMediaPreviewShadow` 为三层中性投影，`UiMediaPreviewRadius` 为 10；氛围光透明度浅色 0.30、深色 0.18，经典皮肤投影为空并保留 0 / 3px 圆角。
- 后台取色与缓存：`src/AvaMedia.Desktop/Controls/MediaPreviewColorSampler.cs`。单路后台解码最长边不超过 64px，128 帧有界缓存，以缩略图内容摘要、文件、时间点和预览尺寸区分；取消过期任务，不读取 UI 持有的位图。编码数据超过 2MiB 或源位图超过 400 万像素时跳过氛围光，保留中性投影，避免昂贵解码。
- 可单元测试的纯计算：`src/AvaMedia.Core/MediaPreviewColors.cs` 的 `Sample` / `AverageBorder`。RGBA 边缘按 alpha 加权，四条外围边分别平均后等权合并；透明边和未完成的格子不参与，内部格子不取色。
- 多宫格接入：`src/AvaMedia.Desktop/ContactSheetWindow.Preview.cs`，逐帧提交已经取得的缩略图字节，后台颜色完成后独立更新光晕，不等待取色再显示预览帧。
- 交互预览流畅优先：`MediaPreviewSurface.UseAmbientLighting=False` 只绘制中性投影，不创建取色解码器、不提交后台取色或逐帧光晕更新。`FitContent=False` 保持裁剪和图片对比视口的原有尺寸与坐标；默认模式包在 Viewbox 外，投影跟随适应后的画面尺寸。
- 推广接入：`EditorWindow.axaml` 的主预览、`BatchCropWindow.axaml`、`BatchRotateWindow.axaml`、`ImageCompressionWindow.axaml`，以及 `Controls/MediaPreviewPanel.cs` 的视频 / 图片视口。共享面板的音频波形、压缩包 / 目录列表不加；`ClipExportWindow` 仅有小缩略图，播放器没有独立封面区且直接呈现连续播放画面，均不加，播放器全屏保持原样。

## 13. 规范内矛盾的处理记录

| 问题 | 处理 |
| --- | --- |
| 旧规范第 4 节“工具少时不提供搜索框”与第 8 节“工具数量少的列表不加搜索框”是有条件的禁止，新约束要求“不提供搜索框” | **已修复**：统一为无条件禁止（第 1 节）；设置、文件浏览、媒体路由、分类、图片浏览、字幕结果及标签 / 词库界面的搜索框已移除，保留类别与状态筛选。 |
| 旧规范“不使用 emoji 作为图标或装饰”只覆盖图标 / 装饰 | **已修复**：扩展为界面任何位置均不使用 emoji；输出路径与预览文案去掉装饰字符，齿轮与网站装饰改用共享 Lucide 路径。 |
| 规范系统蓝 `#007AFF`，而 `Styles/UiStyles.axaml` 中 `UiAccent` / `ThemeAccentColor` 仍为 `#5B5BFA`，`UiNavigationSelection` 亦为 `#5B5BFA` | **已修复**：浅色通用强调色、内置控件和导航选中态统一为 `#007AFF`，深色为 `#0A84FF`；同时迁移悬停、按下和浅蓝配套色，经典皮肤强调色不变。 |
| 网页横幅令牌为 `#FFF8E6`，`Palette.Banner` 实际为 `#FFF6E0`；成功文字令牌 `#248A3D`，`Palette.GreenText` 为 `#1F9D3A`；等待令牌 `#86868B`，`ShellWait` 为 `#8E8E93` | **已修复**：`Palette.Banner` 为 `#FFF8E6`，`Palette.GreenText` 与浅色 `ShellGreenText` 为 `#248A3D`，浅色 `ShellWait` 为 `#86868B`。 |
| 部分代码仍硬编码颜色（如 `ClassificationSubmitStyles.axaml`、`Classification/ClassifyUi.cs`、`RemoteTheme.axaml` 中的样式） | **已修复（所列文件）**：提交样式与 `ClassifyUi` 及其调用界面使用动态主题资源，网页样式 / 模板也使用资源；新资源提供四种皮肤对应值，真实媒体采样色保留。 |
| 直接显示快捷键的键帽、播放器 / 看图帮助及菜单右侧提示 | **已修复**：删除键帽和提示行，帮助改为 tooltip，菜单提示仅在 tooltip；现有键盘命令与 HotKey 保留。全仓未发现虚线拖放框；图表虚线、流程连线与 XP 焦点框保留。 |

## 多宫格截图窗口（A 版）

- 三栏：视频卡片宽 236、中间预览自适应、设置栏宽 312；栏间距 12。设置为“宫格 / 取帧 / 画面 / 输出”四组白色卡片，属性行高 34，紧凑数值控件保留直接输入与步进。
- 视频行含勾选、真实缩略图、文件名、时长和状态；导入、移除、包含子文件夹固定在列表底部。预览卡片顶部为文件名与像素尺寸，下方胶片条可拖动两端，重置范围使用链接。
- 底栏依次为真实生成摘要、打开输出文件夹、关闭、唯一主按钮“生成截图”；生成期间显示停止操作与真实进度。
- 多宫格预览允许以当前参数展示结构：无视频时显示按单格宽高比例排布的浅灰圆角格、格内序号、“选择视频后显示预览”和输出尺寸。这是参数示意，不是假媒体数据；不画插图、emoji、虚线框。
- 抽帧逐格填入，尚未完成的格子柔和明暗过渡；单帧失败显示“无法读取”，整段失败保留占位格并显示错误说明。参数及范围变化防抖 300ms，并取消旧抽帧。
- 所有颜色引用现有 Ui 主题资源；尺寸及圆角由 ContactSheetStyles 提供。现代卡片圆角 14、缩略图及占位格圆角 7；Platinum 无圆角、XP 使用 3px 圆角，保留经典皮肤原生控件和文字。

## 应用内文件选择器（B 版）

- 参考 `/workspace/AvaMedia-sidebar-review/picker/demo-b.html` 的三栏布局：侧栏 220、内容自适应、检查器 270；顶部沿用平台窗口框架，路径导航与视图切换置于 48px 共享标题栏，嵌入模式保留独立导航行；媒体类型分段控件单独一行。侧栏依次为最近使用、个人收藏、位置、设备；收藏添加与移除为次级操作。
- 媒体网格按可用宽度排成等宽列，卡片内边距 8、间距 12，封面比例 16:9；选中浅蓝底、2px 强调色边框。列表行高 54，复选框、缩略图、名称、时长、分辨率、大小、修改时间对齐；悬停使用主题 Hover，键盘焦点可辨认。
- 检查器含真实封面或类型图标、完整文件名、媒体类型与信息行；未取得媒体信息显示“—”，读取失败继续显示类型图标。底栏高 60，左侧真实选择数量与大小，右侧系统选择、取消、唯一主按钮“打开 N 个文件”。文件夹及嵌入模式保留对应动作文字。
- 媒体类型按扩展名计数，筛选说明写明隐藏数量。最近使用视图在文件列表上方提供最多三个真实最近文件夹入口；列表与网格共用选择、键盘和 Shift 范围选择，视图偏好持久化。
- FileUi 的 Fb 主题资源覆盖浅色、深色、Mac OS 9、XP：现代圆角卡片与分段控件，经典皮肤分别使用 0 / 3px 圆角及各自选择文字色，按钮保留宿主原生外观。
- 目录读取、筛选和排序不探测媒体；列表与网格行均虚拟化。布局结束后仅可见文件与检查器发起后台低优先级媒体请求，最多两个同时执行，离开视野、切换目录或筛选即取消；派生结果复用共享磁盘与内存缓存。

### 7.9 通用组件（AvaMedia.Controls）

#### 7.9.0 方案评估与边界

参考 [Devolutions/avalonia-extensions](https://github.com/Devolutions/avalonia-extensions) 的 MacOS 主题（MIT）。上游说明：2026.6.16 为最后兼容 Avalonia 11 的稳定版本，2026.6.17-avalonia12 及以后的版本进入 Avalonia 12，首个 Avalonia 12 稳定版为 2026.6.23。本项目保持 Avalonia 11.3.22、Themes.Simple 和既有皮肤。

| 做法 | 收益 | 成本与皮肤接入 |
| --- | --- | --- |
| 从 2026.6.16 移植所需 ControlTheme | 可复用成熟的模板、交互态和模板部件 | 必须保留 MIT 许可与版权声明；需要改写上游颜色、尺寸和关联资源，并逐个隔离经典皮肤的模板；禁止整体替换应用主题 |
| 自行编写具名 ControlTheme 与组合控件 | 资源契约直接符合本项目，可保持依赖小、业务无关，按组件逐步接入 | 需要维护交互态、键盘焦点与无障碍；标准输入控件沿用 Simple 的交互部件，组合控件负责统一布局 |

**结论：自行编写。** `src/AvaMedia.Controls` 不引用应用其他项目，拥有默认令牌、具名 ControlTheme 和可复用组合控件，结构可独立打包。宿主在默认令牌之后加载自己的资源；Mac OS 9 / XP 用同名令牌覆盖组合控件的尺寸、圆角、阴影，标准控件继续使用经典皮肤模板。未复制 Devolutions 源码，因此不附加其 MIT 声明；以后若移植代码，必须随移植文件保留 MIT 许可与版权声明。

业务界面只选择角色和内容；通用控件的颜色、圆角、高度和内边距在组件资源中定义。既有窗口的专用布局继续按本规范对应章节执行。详见 [组件库说明](src/AvaMedia.Controls/README.md)。

#### 7.9.1 通用组件 demo 规格、资源键与类名

视觉以 `/workspace/AvaMedia-sidebar-review/components/components.html`、`components-dark.html` 及 `sections/s01..s12-light/dark.png` 为准。HTML 中 CSS 变量和标注是规格；尺寸为 pt（1x 逻辑像素）。组件默认值见 [Defaults.axaml](src/AvaMedia.Controls/Themes/Defaults.axaml)，模板见 [ControlThemes.axaml](src/AvaMedia.Controls/Themes/ControlThemes.axaml)，组合角色见 [Components.axaml](src/AvaMedia.Controls/Themes/Components.axaml)。组件预览与状态样例见 `tools/AvaMedia.AiProgressCapture/ComponentGallery.cs`。

| demo 类别 | 主题键 / 类名 / 类型 | 尺寸与状态规格 | 主题资源 |
| --- | --- | --- | --- |
| 01 按钮 | `AcButton`；`Button.ac.primary/secondary/borderless/danger/destructive/icon/ghost`；`SplitButton` | 常规高 28、圆角 14、内边距 0,14、字 13 Medium；主窗口 30；small 高 22、0,10、字 11.5；large 高 36、0,20、字 14；图标 28×28；拆分箭头区 0,8、分隔 0.5 | `UiAccent/UiAccentText/UiPrimaryHover/UiPrimaryPressed`；`UiFill/AcFillHover/AcFillPressed`；危险 `AcDangerSoft/AcDangerHover/AcDangerPressed/UiDanger`；无边框 `AcSelection/AcSelectionPressed`；焦点 `AcFocusShadow` 外环 3；禁用 `AcDisabledOpacity` 0.4 |
| 02 下拉与可编辑组合框 | `AcComboBox`、`AcComboBoxItem`；`ComboBox.ac`，`.pulldown`；`AcMenuItem`、`ContextMenu.ac` | 高 24、圆角 6、内边距 0,4,0,10；双箭头蓝块 16×16、圆角 4；编辑箭头区宽 20；菜单圆角 10、内边距 5；行高 24、圆角 5、勾选列 14、分隔 0.5（缩进 10） | `AcField/AcPopupFace/AcHairline/AcMenuFill/AcPopupShadow`；高亮 `UiAccent/UiAccentText`；禁用 `AcTextTertiary`；聚焦外环，编辑输入保留 |
| 03 复选、单选、开关 | `AcCheckBox/AcRadioButton/AcToggleSwitch`；`.ac`、`.error`、开关 `.small` | 选择框 14×14、圆角 3.5（单选圆形）、标签间距 7；单选中心点 6；开关 38×22、圆角 11、白旋钮 18、按下加宽 4；small 26×15、旋钮 13；支持混合、选中悬停/按下、焦点、错误、禁用 | `AcField/AcChoiceBorder/UiAccent/UiAccentText`；开 `UiSuccessFill`、关 `UiTrack`；旋钮 `AcKnob/AcKnobShadow`；焦点 `AcFocusShadow` |
| 04 数值步进器 | `AcNumericUpDown/AcButtonSpinner`；`NumericUpDown.ac` | 字段高 24、默认宽 72、圆角 6、右对齐数字 13；独立步进区 15×22、圆角 5、两段各 11；间距 4；单位字 12；错误 1px 红边与 3px 外晕、消息字 11 | `AcField/AcHairline/AcPopupFace/AcFillPressed`；`UiNumericFont` 数字；`AcErrorShadow/UiDanger` |
| 05 文本框 | `AcTextBox`；`TextBox.ac`、`.multiline`、`.error` | 单行高 24、圆角 6、内边距 0,8、字 13；多行最小高 76、内边距 6,8、行高 1.45；描边 0.5；错误消息间距 6；禁用不透明度 0.45 | `AcField/AcHairline/AcFieldHoverBorder/AcTextTertiary`；聚焦 `UiAccent/AcFocusShadow`；错误 `UiDanger/AcErrorShadow` |
| 06 滑块 | `AcSlider`；`Slider.ac`、`.ticks`；`RangeSlider` | 横向控件高 22，垂直方向交换轨道与刻度的尺寸；轨道 3、圆角 1.5、白圆旋钮 18；刻度 1.5×5、刻度旋钮宽 14 尖底；范围双旋钮；按下放大 1.08、焦点环、禁用 0.4 | `UiTrack/UiAccent/AcKnob/AcKnobShadow/AcFocusShadow/AcTextTertiary` |
| 07 进度与阶段 | `AcProgressBar`；`ProgressBar.ac.paused/failed`；`ProgressRing/LoadingSpinner/StageIndicator/SubStepRow/StatusGlyph` | 条高 4、圆角 2；不确定滑块 30%、1.2s 往返；旋转器 14/22；进度环 44、线宽 3.5；阶段高 64、圆角 10、内边距 10,12、序号 18、字 10.5 半粗、箭头区 24；当前边 1.5；计时字 11；子步骤图标 14、间距 9、上下 7、标题 13、说明 11 | `UiTrack/UiAccent/UiSuccessFill/UiWarning/UiDanger/UiAccentSoft`；`AcStageRadius/AcStagePadding/AcFocusThickness/AcStageLine/AcStageLineBrush`；计数等宽；分类进度专用非当前阶段无边框 |
| 08 分段与标签栏 | `SegmentedControl`、`.tabs`；`AcSegmentItem` | 高 28、外圆角 8、内边距 2、段圆角 6、内边距 0,14、字 12.5 Medium；段间 0.5 分隔（选中相邻隐藏）；标签字 13、间距 22、底间距 9、指示条高 2 | `UiFill/AcSegmentFace/AcSegmentShadow/AcHairline/UiAccent`；深色选中 #636366；悬停、焦点、禁用 |
| 09 状态胶囊与徽标 | `StatusPill/CountBadge`；`.ac-status`、`.ac-badge`、`.dot` | 胶囊高 20、圆角 10、内边距 0,8、字 11.5 Medium、状态点 6；徽标高/最小宽 18、圆角 9、内边距 0,5、字 11 半粗等宽；通知点 8；叠放外边 1.5 | `AcRunningFill/AcSuccessFill/AcWarningFill/AcDangerFill/AcWaitingFill`，对应色 12–14%；文字 `UiAccent/UiSuccess/AcWarningText/UiDanger/UiTextSecondary` |
| 10 卡片、设置分组、列表 | `Card/ListRow/SectionHeader`；`.ac-card/.ac-group/.ac-setting-row/.ac-list-row/.ac-section` | 卡片圆角 12、内边距 16、内部/卡片间距 12；标题 13 半粗、描述 11.5、下间距 8；设置分组圆角 10、行最小高 40、内边距 0,12、分隔 0.5 左缩 12；列表高 56/36、圆角 8、内边距 0,10、间距 8、缩略图 40/圆角 6、角标高 16、数值 20 Bold 右对齐 | `UiSurface/AcCardFace/AcCardShadow/AcHairline/UiFill/AcSelection`；活动选择 `UiAccent/UiAccentText`；常态、悬停、非活动选择、活动选择、禁用 |
| 11 浮层、表单与通知 | `AcToolTip/AcMenuItem/AcPopover`；`.ac-tooltip/.ac-popover/.ac-sheet/.ac-notice`；`Notice/Popover` | 提示字 11.5、内边距 4,8、圆角 6、延迟 0.6s；气泡圆角 12、内边距 14、默认宽 240、箭头 12；确认表单宽 300、圆角 16、内边距 20,18,16、图标 44、标题 13 Bold、按钮间距 8；通知宽 340、圆角 10、内边距 10,12、色条宽 3、图标 16 | `AcMenuFill/AcPopupShadow/UiText/UiTextSecondary`；通知 `AcNoticeFace/UiAccent/UiSuccessFill/UiDanger`；警告 `AcBanner/AcBannerText/UiWarning` |
| 12 空/错误状态与滚动条 | `EmptyState`、`.error`；`AcScrollBar`、`ScrollBar.ac` | 空/错状态居中图标 40、线宽 1.4、标题 13 半粗、说明 11.5、间距 6、小按钮；滚动条常规滑块宽 7、圆角 3.5、距边 2；悬停宽 11、轨道宽 15、描边 0.5；静止 1s 后淡出 | `UiCanvas/AcTextTertiary/UiDanger/AcScrollBrush/AcScrollHover/AcScrollTrack/AcHairline`；深色滚动条 40% 白，悬停 50% 白 |

标准控件通过 `.ac` 接入，亦可直接指定具名 Theme；独立项目只依赖 Avalonia 与 Themes.Simple。类名 `ac-hover/ac-pressed/ac-focus` 为预览提供确定的状态快照，和真实伪类共享同一资源与样式。皮肤资源覆盖见 `src/AvaMedia.Desktop/Styles/ComponentSkins.axaml`；Mac OS 9 / XP 的标准输入继续使用经典模板，组合组件使用同名资源覆盖圆角、颜色和阴影。预览页允许明确标注的示例数据，实际窗口仍只显示真实数据。浮层材质以主题半透明画刷实现跨平台回退。

#### 7.9.2 进度与阶段

- 确定和不确定进度均为 4px 条形。不确定条为 1.2s 往返滑块，行内禁止用环形替代条形；百分比放在标题或进度右侧，使用等宽数字，不叠在进度条上。旋转器为 0.8s 周期；减少动态效果由继承属性 `ComponentMotion.Reduced` 停止旋转与往返，保留静态进度。
- 步骤横向等宽，均高 64，说明最多两行省略并提供完整 tooltip；步骤间 12px 灰色箭头居中。右上计时、计数使用 `UiNumericFont` 和 `tnum`。
- 分类进度窗口只在标题显示百分比，阶段卡片下不添加独立进度条。子步骤的进行中弧形仅表示该子步骤活动。

参考代码：[StageIndicator.cs](src/AvaMedia.Controls/StageIndicator.cs)、[Surfaces.cs](src/AvaMedia.Controls/Surfaces.cs)、[SegmentedControl.cs](src/AvaMedia.Controls/SegmentedControl.cs)。

```xml
<Button Classes="ac secondary" Content="临时停止" />
<Button Classes="ac danger" Content="结束任务" />
<ProgressBar Classes="ac" Value="33" Maximum="100" />
```

```csharp
var card = new AvaMedia.Controls.Card { Child = content };
var stage = new AvaMedia.Controls.StageIndicator();
stage.Update(1, "逐个文件分析", "按添加顺序处理文件", "412 / 1284", "running");
```

#### 7.9.3 分类媒体内容与列表

- 预览使用 `MediaPreviewSurface` 的统一投影；背景 `AcPreviewFill`、圆角 10，交互预览关闭氛围光。检测标签用白底胶囊、`UiText` 类别和 `UiAccent` 置信度文字，细投影；场景标签为半透明深色底与白字，不使用蓝色置信度块。
- 真实候选画面等宽排列，缩略图圆角 6、当前项 2px 强调色框。服装主色比例条高 `AcColorBarHeight` 8、圆角 4，说明置于下方；实际采样色允许直接显示。归组理由浅蓝底，左侧强调色半粗标题，正文允许换行。
- 文件流单行 8 个真实缩略图，已完成右下角 14px 绿色圆形勾，当前 2px 蓝框，等待透明度 `AcQueuedOpacity` 0.4；不补假数据。
- 分组采用通用列表行：40px 叠放缩略图和右上蓝色 `+N` 胶囊、两行信息、右侧 20px 粗体计数与“个文件”。组数增加后在卡片内滚动，不能挤压末行。只有真实有变化才显示 `+N`。
- 分类与字幕等任务共用 `JobRowView` 的行模板、状态胶囊和 4px 进度，入口按钮统一为“查看任务”。

实现引用：[分类进度窗口](src/AvaMedia.Desktop/Classification/ClassificationRunWindow.Run.cs)、[文件流](src/AvaMedia.Desktop/Classification/ClassifyWall.cs)、[任务行](src/AvaMedia.Desktop/Controls/JobRowView.axaml)。参考 `/workspace/AvaMedia-sidebar-review/**/demo-*.png` 与对应 HTML，以及 `ai-progress/v2/progress-from-tasklist-desktop.html`；该 HTML 中说明文字依真实数据变化，不能为匹配截图伪造分析结果。

#### 7.9.4 宿主接入与预览

桌面通过 `Styles/ModernComponents.axaml` 将普通按钮、选择、输入、滑块、进度和滚动条接到通用主题；专用导航、传输与窗口按钮保留角色模板。`Ui` / `ClassifyUi` / `SpeechUi` 只负责选择角色和内容，字幕提交/进度卡片改用 `Card`。`Motion.cs` 向组件传递减少动态效果偏好；`ScrollVisibility` 在停止活动 1s 后淡出覆盖式滚动条。经典皮肤仍由 `Platinum*.axaml` / `WindowsXP*.axaml` 提供标准控件，`ComponentSkins.axaml` 覆盖组合表面和状态令牌。

`tools/AvaMedia.AiProgressCapture --gallery-view` 打开可操作、可切换浅深色的 12 类组件预览；`--gallery` 截取所有区块。截图使用 zh-CN、DISPLAY=:11、AVAMEDIA_CHROME=mac、XDG_DATA_HOME=/tmp/avademo/data，输出到 `/workspace/AvaMedia-sidebar-review/components/impl/`；`compare_components.py` 生成左 demo / 右实控件的 `compare-s01..s12-light/dark.png`。整体 gallery 与分类窗口另交付到 `ai-progress/v3/`。

#### 7.9.5 空状态与错误状态（补充 9.3）

- 分组/结果尚无内容：居中 40px 状态符号和一句说明，不留空白。
- 单项失败：本行红色状态符号和真实原因，卡片保持原表面颜色。
- 任务失败或取消：标题百分比旁显示红色/灰色状态胶囊，隐藏暂停与结束按钮，只保留关闭/查看日志；已完成状态保持结果页操作。

## 窗口内工具栏菜单栏（B 版）

- 参考 `/workspace/AvaMedia-sidebar-review/menubar/menubar.html` 的 B 区域及浅深色截图；使用已有 52px 平台标题行，不另建标题栏，不实现 A / C 方案。菜单放在标题行左侧，标题居中；任务计数放在下方任务栏，为中文菜单和 Windows 窗口按钮留出空间。文字菜单高 26、字 13、左右内边距 10、间距 6、圆角 7。正常透明，悬停 Fill，展开 FillHover 与 Medium 字重，禁用使用三级文字色。
- 下拉菜单位于工具栏底边下方 4px，最小宽 220、内边距 5、圆角 10；行高 24、圆角 5、左内边距 6、右 10、勾选列 14、列间距 6。选择行使用强调色与强调文字色；禁用项使用三级文字色，分隔线 0.5、上下间距 5、左右缩进 10。子菜单沿用同一模板与右箭头；快捷键只在提示中显示。跨平台弹出表面使用 demo 玻璃色与页面底色合成后的实色（浅色 `#F5F5F8`、深色 `#2A2A2E`），避免没有背景模糊的弹窗透出任务文字。
- `Styles/ToolbarMenus.axaml` 提供 `Mb*` 颜色、尺寸、圆角与阴影资源；浅色 / 深色按 demo，Platinum / XP 沿用各自原生菜单主题。菜单和 macOS 原生菜单共用命令树，保留 ff28d81 的文件、编辑、窗口、帮助及其命令顺序；原有队列、显示、转换等操作追加到相应分组。
- macOS 原生菜单已导出时隐藏窗口内菜单，避免同一命令占据两处菜单栏；未导出时保留窗口内菜单保证命令可达。`AVAMEDIA_CHROME=mac` 仅预览窗口框架，不代表原生菜单已经存在。

## 设置窗口（v2）

- 依据 `/workspace/AvaMedia-sidebar-review/settings/settings-v2.html` 与浅深色参考图，分类顺序为通用、外观与声音、播放器、转换与性能、本地模型、在线 AI、远程管理、高级、关于。侧栏宽 230（含内边距），导航行高 30，分类间两处 10px 间隔；内容内边距 26，页标题 15 半粗，分组标题 12，属性行最小高 40、内边距 12，组圆角 11。沿用公共平台窗口框架，不复制窗口标题栏。
- 优先采用 AvaMedia.Controls 的分段控件、分组表面和状态胶囊；属性开关在行末，说明 11px 次文字；子行缩进 18，依赖项禁用但保留可见。CPU 预留和图片质量采用滑块与右侧数值，预热比例保留紧凑步进器。
- 不设置访问令牌，局域网开启时提示“服务不要求令牌，只在可信网络中开启”；地址与客户端只显示真实数据。Beta 默认开启。模型管理保留独立的可达入口。
- 线程选择含自动及原有 1–16 的完整值；旧配置保持原来的明确线程数。更新方式合并为三项，未修改时保留旧标志。恢复默认只重置窗口显示的偏好，保留激活状态、检查时间及隐藏工具路径。
- 主题新增跟随系统，强调色色板通过 Skin 覆盖已有 Ui / Shell / Qc / Avalonia 主题资源，全应用共享；色板预设为蓝、紫、粉、红、橙、黄、绿、灰，经典皮肤保留其原生强调色。
