# 设置窗口 v2 截图

仅展示真实设置和模型状态，不创建任务、不探测媒体、不执行更新或下载。

```sh
DISPLAY=:11 AVAMEDIA_CHROME=mac XDG_DATA_HOME=/tmp/avademo/data \
  ~/.dotnet/dotnet tools/AvaMedia.SettingsCapture/bin/Debug/net8.0/AvaMedia.SettingsCapture.dll \
  /workspace/AvaMedia-sidebar-review/settings/impl/
python3 tools/AvaMedia.SettingsCapture/compare.py /workspace/AvaMedia-sidebar-review/settings
```

浅色和深色各保存 9 个分类，`compare-*.png` 左侧为 v2 demo，右侧为桌面实现。通用、模型、在线 AI 采用较高窗口以展示完整设置；其他页为 900 × 640 逻辑像素。截图取自指定 DISPLAY 的实际窗口像素；对照图将 demo 按相同逻辑宽度缩放，避免离屏渲染漏掉部分模板内容。

模型页保留「查看模型库」入口，展开后仍可使用原来的筛选、下载、导入、校验和删除操作。截图不填充演示中的示例客户端和已检查时间。
