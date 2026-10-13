# 未完成任务

更新于 2026-10-13。

## 进行中

- 多宫格截图字体选择，以及预览低清、导出高清（后台任务）。已提交 `3da0445`、`ecf3abe`、`2af6879`，`ContactSheetFonts.cs`、`ContactSheetFrames.cs` 还有未提交的改动，Codex 正在收尾。报告：`/workspace/AvaMedia-sidebar-review/sheetfont/report.md`。

## 待修复

- Linux 下使用 mac 外框预览时，帮助等工具窗口同时出现系统装饰和自绘标题栏，需要统一到 `WindowChrome.ApplyPlatformFrame`。
- 任务行的开始、暂停、停止、恢复、重试操作只做了构建和截图，需要用真实任务跑一遍。
- 普通已完成任务还没有 demo 里的缩略图和完整详情。
- 公共预览空态黑底的问题已交给 T3，需要复查剪辑和转换窗口是否都已生效。
- 审计修复新增的文案键需要再核对一遍英文翻译是否齐全。

## 需要真机验收

- Windows 11：所有窗口有系统原生圆角和阴影；最大化、还原、贴边、跨屏 DPI 后形状正常；主窗口最小化后独立工具窗口仍可见、能单独切换。
- Windows 10：窗口直角加阴影，任务栏能单独切换。
- 字体：Win11 Segoe UI Variable 配雅黑的混排效果；Win10 缺少这些字体时的回退；macOS 实际命中 SF 和苹方，内置 Noto 只用于缺字回退。
- 天池播放器独立窗口在真实 macOS 上的行为（红绿灯、全屏、位置记忆）。
- 浏览器嗅探窗口在 Windows 和 macOS 上的真实浏览器内核。
- PDF 工具正常加载 PDF 的界面（测试机缺 libpdfium，未验收）。
- 下次发版时检查 Windows 和 macOS 安装包：瘦身后内容完整、能正常启动。

## 待决定

- 显卡版（Vulkan）语音识别约 56 MB，是否改成按需下载。
- 是否去掉 Noto Sans SC 的 Medium 字重（省约 2.4 MB）。
- macOS 的 ONNX Runtime 如果是 Intel + ARM 通用版，是否裁成只保留 arm64（约省 15 MB）。
