# 组件预览截图

只保留通用组件预览（gallery）。旧的 AI 进度截图（字幕提交/进度窗口、分类进度窗口、`--classification`、`sidebyside.py`）已随旧进度视图一起删除；多阶段任务窗口的截图见 `tools/AvaMedia.MultiStageCapture`。gallery 中的阶段指示与子步骤区块（旧 `StageIndicator` / `SubStepRow`）留空。

## 通用组件预览与 demo 并排对照

`--gallery-view` 打开可操作的 12 类组件展示页，右上角切换浅色/深色。`--gallery` 用真正的 AvaMedia.Controls 控件截取浅色与深色共 24 个区块，不使用 HTML 截图充当实现。

```sh
DISPLAY=:11 AVAMEDIA_CHROME=mac XDG_DATA_HOME=/tmp/avademo/data \
  ~/.dotnet/dotnet run --project tools/AvaMedia.AiProgressCapture -- \
  /workspace/AvaMedia-sidebar-review/components/impl --gallery
python3 tools/AvaMedia.AiProgressCapture/compare_components.py
```

输入规格为评审目录中的 `components.html` / `components-dark.html`。`export_components.py` 仅提取控件位置、示例内容、字体标注与文字语义资源键到 `component-layout.json`，所有控制模板、状态、表面均由 Avalonia 渲染。比较脚本将 2x demo 缩到相同 1x 逻辑尺寸，生成 `compare-s01..s12-light/dark.png` 和 `gallery-light/dark.png`，不裁切组件。

调用方提供的 XDG 目录保留使用，未提供的目录在临时环境中创建。
