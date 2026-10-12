# 人物检测任务详情截图

只用于人物详情 B 版评审，不运行回归测试。用真实视频运行 NanoDet，保存完成结果与检测过程中实际发布的部分结果，然后在指定 X11 显示器截取窗口。失败原因明确标为界面状态演示，部分片段和中断位置仍来自真实观测。

```sh
DISPLAY=:11 AVAMEDIA_CHROME=mac XDG_DATA_HOME=/tmp/avademo/data \
  AVAMEDIA_CAPTURE_MODELS=/home/box/.local/share/AvaMedia/models \
  AVAMEDIA_CAPTURE_FFMPEG=/tmp/ffmpeg-n9.0-latest-linux64-gpl-9.0/bin/ffmpeg \
  ~/.dotnet/dotnet run --project tools/AvaMedia.PersonTaskCapture \
  -p:EnableWindowsTargeting=true -- \
  /workspace/AvaMedia-sidebar-review/person-task/impl \
  /workspace/AvaMedia-sidebar-review/ambient/samples/航天员.mp4
```

模型和 FFmpeg 环境变量可省略，默认使用应用解析的路径。抽帧须使用应用要求的新版 FFmpeg。截图依赖 `xdotool` 和 Python Pillow；捕获真实原生窗口，避免离屏渲染坐标误差。追加第三个参数 `people.json` 直接重放已保存结果，不重复分析视频；同目录的 `partial.json` 和 `capture-source.json` 保存部分结果与实际耗时。

`compare.py <评审目录>` 将 demo 与实现等比缩到同一宽度后并排输出 `impl/compare-light.png`、`compare-dark.png` 和 `compare-states.png`，不裁剪界面。截图、媒体与任务结果保留在仓库外。
