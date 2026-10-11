# 网页端到端验证

仅在用户明确要求浏览器/媒体验收时运行 `scripts/Verify-WebConsole.cjs`，不纳入默认 CI 或日常编译检查。脚本使用真实 Chromium 浏览器和现有 AvaMedia 服务，操作网页完成文件浏览、NSFW 参数/结果查看、两秒转换、任务控制、预览和下载、断网恢复及结果/日志读取；不使用伪造模型结果。

需要 Node.js、Playwright（含 `playwright/test`）和 Chrome。服务建议使用独立 `--mcp-state-directory`、独立输出目录和空闲端口，复用本机已安装模型。保持原始媒体不变；不要导入或启动日常队列中的未完成任务。

```sh
NODE_PATH=/path/to/node_modules node scripts/Verify-WebConsole.cjs \
  --url http://127.0.0.1:18921 \
  --video /absolute/path/test-video.mp4 \
  --output /absolute/path/acceptance-output \
  --evidence /absolute/path/acceptance-evidence
```

默认从网页提交 NSFW 识别任务，等待真实推理完成。若在同一验收会话中已经通过网页提交并完成识别，可传 `--existing-nsfw-task UUID` 继续检查结果和其它流程，避免重复推理。输入须为可处理的视频，服务器已启用 NSFW 且具备所需模型。脚本会实际生成转换输出和下载文件，保存源文件前后 SHA256、浏览器错误、任务记录、截图与 `acceptance.json`。发生失败时保留证据及已有任务，修复后再继续。

脚本针对同机浏览器/服务器验证：路径用于读取原文件哈希，并直接核对下载与服务器输出。它不代表异地公网、反向代理或多操作系统验收。

## 2026-10-11 实际验收

- 使用本机 Chrome / Chromium 和独立后台服务，访问 Downloads 下的实际测试目录；网页进入 `pikpak` 与 `FavPIC`，从网页提交两段视频的真实 NSFW 分析，两项均完成。原目录中明确标注泄露的文件没有用于推理。
- 两段视频各采样 8 帧，JoyTag 与 Marqo NSFW 均返回真实结果，后端为 Core ML / CPU。网页显示独立 NSFW 分数、阈值、时间点及内容标签；两项最高分均约 94.7%，平均分分别约 72.5% 和 52.7%。这些数值证明结果成功传递与展示，不代表已人工逐帧确认分类准确率。
- 从网页加入转换任务，验证等待 → 暂停 → 恢复 → 完成，实际输出 2 秒、320×427 的 MP4。浏览器成功读取视频元数据，网页下载文件与服务器输出 SHA256 一致。
- 在服务接收请求后主动中断浏览器响应，刷新页面并恢复同一请求，返回原任务 ID；随后验证停止、重试及最终完成。
- 验收队列 6 条实际记录均可打开详情与日志，包含两项内容识别、一项导入的已完成桌面视频总结、三项转换记录（其中一项为验收脚本首次中断后留下的等待任务，检查后已停止）。同时验证完成状态筛选和不存在目录的错误反馈。并未逐一执行所有工具的完整媒体处理回归。
- 浏览器脚本错误为 0；两段源视频前后 SHA256、大小和修改时间均一致。Desktop Release 编译 0 警告、0 错误，网页/验收脚本语法检查通过。

本地证据保存在忽略目录 `artifacts/web-console-e2e-20261011/`：`browser/acceptance.json`、`browser/nsfw-result.json`、`browser/nsfw-result.png`、实际输出和下载文件、`sources-unchanged.json`。没有覆盖日常设置或队列；视频总结只读复制已完成任务记录用于结果查看，未重新执行。

## Avalonia 与当前实现

仓库使用 Avalonia 11。它支持独立 Browser 项目，通过 `Avalonia.Browser` 和 .NET WebAssembly 运行控件，但仍受浏览器沙箱约束。现有桌面窗口不会自动成为网页，FFmpeg、ONNX、本机目录操作仍需宿主服务。

当前网页使用随 MCP 宿主打包的 HTML/JavaScript 视图，与 Avalonia 桌面共享持久任务队列和媒体/模型服务；后续可另接 Avalonia Browser 视图和远程 `IFileBrowserSource`。参见 [Avalonia 11 WebAssembly 官方文档](https://v11.docs.avaloniaui.net/docs/guides/platforms/how-to-use-web-assembly/)。
