# AvaMedia 功能图标 v2

本套包含 20 款独立的透明 PNG，使用 Codex 内置 `image_gen.imagegen` 参考 AvaMedia 原有矢量图标生成。原图尺寸均为 1254 × 1254；复制进项目时未改动图像像素。没有导入格式工厂图标。

风格沿用原有蓝色胶片、绿色箭头、橙色齿轮、红色剪刀及白色折角文件，增加少量高光和立体边缘。最先生成的 `rotate.png` 为后续图标的风格参考。

## 文件与功能

| 文件 | 对应功能 |
| --- | --- |
| video.png | 视频格式转换 |
| formats.png | 更多视频格式 |
| join.png | 视频合并／混流 |
| gear.png | 视频优化 |
| split.png | 音视频分离 |
| crop.png | 画面裁剪 |
| rotate.png | 视频旋转／自动摆正 |
| clip.png | 快速剪辑 |
| erase.png | 去除水印 |
| frames.png | 提取视频帧 |
| record.png | 屏幕录制 |
| player.png | 视频播放器 |
| download.png | 视频下载 |
| audio.png | 音频格式转换 |
| image.png | 图片格式转换 |
| document.png | 文档格式转换 |
| archive.png | 压缩工具 |
| disc.png | 光盘工具 |
| info.png | 媒体信息 |
| clip-list.png | 批量剪辑 |

## 使用与复现

`FeatureIcon` 从嵌入的 Avalonia 资源加载图像，缓存每种图标的 256 像素显示副本；实际显示使用高质量缩放。MP4、MKV、MP3、PNG、PDF 等格式标签继续由控件绘制，原始 PNG 不含格式文字。功能图标缺失时使用原有矢量实现。

[manifest.json](manifest.json) 保存每款图标的完整生成提示词、工具模式、SHA-256 和原图尺寸。原有风格参考位于 [feature-icons-v1-reference.png](../../../../../docs/assets/feature-icons-v1-reference.png)，新图标总览位于 [feature-icons-v2-preview.png](../../../../../docs/assets/feature-icons-v2-preview.png)。

质量检查包括：20 款素材的透明边角、浅色背景的 64 像素渲染、深色背景的 48 像素渲染，以及桌面程序中功能入口的实际渲染。生成过程采用内置图像工具，没有使用 CLI 图像生成或 Python 修改图片。
