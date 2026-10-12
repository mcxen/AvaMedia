# 下一版本发布说明草稿

- 移除独立“导出帧”工具：清理桌面与远程网页入口、媒体路由、MCP 能力表单、专属任务类型、间隔设置、执行分支及相关测试用例。
- 单帧图片通过天池播放器的“截取当前帧”按钮、菜单或 Ctrl+E / Cmd+E 保存；保留 PNG、重名避让、VR 当前视角与原生播放器截图。
- 保留播放器、编辑器、AI 分析、多宫格截图使用的共享抽帧服务、FFmpeg 命令构建和图标资源。不保留旧导帧任务或设置的兼容与迁移分支。
- 本地检查：Desktop 与 Browser 使用 `~/.dotnet/dotnet build -p:EnableWindowsTargeting=true` 编译通过；Desktop 0 警告，Browser 保留 2 条既有 `_next` 字段警告。MCP 契约测试 20 项、播放器截图与入口移除定向检查 17 项通过；删除用例所在的 FunctionTests / SmokeTests 编译通过，未运行全量功能、截图或性能测试。
