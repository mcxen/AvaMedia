# 视频预览并行验证与修复

2026-10-13。用户明确授权多个 Agent 分别运行本次预览能力测试。基线为已推送的 `8ee745d`，在独立 worktree 中运行；三个 Agent 只写各自 artifacts，生产修复由主 Agent 完成。没有运行全量套件、人物模型或媒体导出。

## 验证结果

| 范围 | 运行证据 | 最终结果 |
| --- | --- | --- |
| 快速剪辑与调度器 | 47 个生产调度器、EditorWindow、PreviewDrag、FilmstripTimeline 的定向断言 | 全通过 |
| 其他桌面视频工具 | 5 个生产控件的 22 个运行场景，另加厨房真实帧 | 全通过 |
| 真实缩略图与远程服务 | 19 个检查组、61 条观测，真实 FFmpeg、HTTP 和 3 次 MCP RPC | 初轮发现两处小源放大；修复后 12 条受影响观测通过 |
| 处理后旋转预览 | 90°/270° × regular/compact/drag，另加不旋转控制 | 初轮 6 项超预算；修复后 7 项全通过 |
| 文件选择器最终位图 | JPEG/PNG × 网格/列表 × 64×36、32×18、640×360 三种封面尺寸 | 修复后 12 项全通过 |

桌面控件测试在 Avalonia Headless 中实例化生产窗口，并触发真实 Slider/Thumb/Filmstrip 指针事件。模拟解码器用于可重复地控制解码延迟和取消时序；厨房视频及旋转尺寸复测使用生产 MediaEngine/MediaVisualPreview 与真实 FFmpeg。报告分别标注这两层，不能把模拟时延当成 FFmpeg 性能。

厨房视频的首次真实 FFmpeg 拖动：2.2 秒、142 次指针移动，松手前显示 13 帧，最后准确请求 112.959290 秒。拖动预算为 384×216，竖屏实际帧约 122×216；松手实际帧为 270×480。后续组合运行松手前显示 12 帧。这些是一次具体运行的连续更新证据，不是原生窗口帧率或保证值。源视频 SHA-256 在各轮前后保持一致。

生命周期检查涵盖活动解码不被每次移动取消、仅保留最新等待位置、松手/捕获丢失只完成一次、边界图只刷新变化侧、换源/换轨、关闭/Dispose、取消后晚到的结果不回写。BatchCrop 保存的裁剪坐标仍依据源 1920×1080，测试只创建裁剪请求，未输出视频。

## 本轮发现并修复

1. `MediaThumbnailCache` 原先固定 scale 到请求宽度，把 64×36 源放大为 320×180。现在以真实显示宽度限制缩放，并输出方像素；缓存身份改为 thumb-v2，文件选择器媒体缓存改为 picker-media-v3。
2. MCP `avamedia_editor_preview` 未使用源尺寸限制，把 64×36 源放大为 640×360。现在使用 `VideoPreviewSizes.Bound` 并沿用 Probe 选中的视频轨，真实 MCP 复测输出 64×36。
3. `MediaVisualPreview` 在旋转前计算尺寸预算；FFmpeg 先 scale 后 transpose，导致 90°/270° 成品越界。现在用旋转后的投影宽高计算比例，仍保存旋转前的 draft 尺寸。修复后 regular/compact/drag 分别为 270×480、180×320、122×216；未旋转控制仍为 640×360。

4. 文件选择器显示层固定以 320/96 解码，又把 64×36 JPEG 放大为 320×180/96×54。现在从 JPEG SOF 或 PNG IHDR 读取编码封面宽度，限制解码宽度；依据封面本身，不用原视频分辨率代替。生产 FileBrowserCell.ApplyMedia 的 12 项复测通过：64×36 与 32×18 保持原大小，640×360 分别按网格/列表降到 320×180/96×54。

小视频、SAR、实际 90° display matrix、竖屏和无 SAR 字段视频都运行了受影响复测。厨房文件选择器封面为 320×568；热缓存请求没有再启动 FFmpeg。首轮旋转 fixture 的 metadata 标记未生效，已纠正并用独立 ffprobe 核实实际 display matrix，报告保留原始证据并明确更正。

厨房三组小样本中，960×540 原预算的输出为 304×540，新拖动输出为 122×216：PNG 像素约减少 84%，字节约减少 80%。冷抽帧中位数约 183 ms → 161 ms；样本少、顺序固定且多 Agent 并行负载会干扰，仅作为观测，不能推导固定提速倍数。

## 证据与限制

完整本机报告及可复跑 harness：

- [快速剪辑](/Users/mcx/Documents/OpenSpring/AvaMedia/artifacts/preview-tests-20261013/quick-clip/report.md)
- [桌面工具](/Users/mcx/Documents/OpenSpring/AvaMedia/artifacts/preview-tests-20261013/desktop-tools/report.md)
- [显示层解码](/Users/mcx/Documents/OpenSpring/AvaMedia/artifacts/preview-tests-20261013/desktop-tools/file-cell-post-fix-report.md)
- [真实缩略图、HTTP、MCP](/Users/mcx/Documents/OpenSpring/AvaMedia/artifacts/preview-tests-20261013/thumbnails/report.md)

主 Agent 另打开深色原生窗口，厨房预览正常显示，时间线 AX 点击准确定位 94.383 秒，实际帧为 270×480。CUA 坐标拖动和后续重启没有形成有效证据，故不计入持续拖动验收；本轮持续更新结论来自上面的生产 Headless 指针链与真实 FFmpeg。没有宣称已安装版本已更新或完成原生拖动手感验收。

修复后受影响 Desktop（含 Core、Mcp、MediaUi、FileUi、Controls）构建通过，0 错误、0 警告；最新 FileUi 与 Browser 构建通过，Browser 保留两条既有 `_next` 警告；修改块 whitespace 检查通过。没有因已通过的测试而重新运行无关或全量检查。
