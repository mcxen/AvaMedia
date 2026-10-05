# 已确认的 v2 图标风格

项目是经典桌面多媒体工具 AvaMedia。本批次由原有矢量图标引导，先生成 `rotate.png`，再以它和原有风格总览作为参考生成其他功能。用户已明确认可整批图标。继续生产时维持同系列风格，不重做视觉方向。

## 外观

| 元素 | 基准 |
| --- | --- |
| 外轮廓／胶片 | 炭灰 `#26333D` |
| 主体蓝色 | 青蓝 `#22A3D0` |
| 方向／提取箭头、音符 | 草绿 `#68AE3A` |
| 播放键、齿轮、加号 | 金橙 `#F6AB32` |
| 剪刀柄、录制圆点 | 珊瑚红，沿用 `clip.png` / `record.png` |
| 文件、边框、剪刀刃 | 银白 `#E6F0F6` |

正面为主，深色边缘，轻微倒角、柔和高光，几何轮廓清楚。不要夸张透视、照片材质、厚重投影或玩具塑料光泽。功能主体约占方形画布 80%，应用标识约占 85%；四周透明留白，主体不碰边。

功能图标在界面以 48–64 像素呈现，使用少量大部件。剪辑使用剪刀，画面裁剪使用带角标的取景框；旋转箭头方向明确；合并用加号；录屏用红点。参考对应现有图标决定符号，避免因复用其他功能图标而改变含义。

当前功能 PNG 均为 1254 × 1254，这是本次工具的实际输出尺寸，不是以后生成必须支持的尺寸参数。内置工具不接受 CLI 的质量、尺寸或输出路径控制参数。

## 提示词骨架

```text
Use case: stylized-concept. Asset type: ONE transparent PNG feature icon for AvaMedia desktop software.
Input image 1: approved feature-icon contact sheet, style reference only.
Input image 2: approved rotate icon, palette, bevel and highlight reference only.
Match the approved classic desktop utility family: almost frontal, charcoal edges, cyan blue,
light silver, appropriate green/orange/red accents, restrained bevels and subtle highlights.
Optimize for 48–64 pixels: bold simple silhouette, few large components, balanced visual weight,
single centered icon occupying about 80% of a square canvas, clean padding.
True alpha transparency in all empty background pixels.
No text, letters, watermarks, checkerboard pattern, opaque background, contact sheet or heavy shadow.
Primary request: <function>. Subject: <unambiguous main symbol and necessary action cue>.
```

应用图标使用 `logo-brand` 和应用 manifest 的完整提示词。只有信息图标允许标准的白色 `i` 符号。格式文字和界面标签不交给生成模型。

## 接入位置

功能类型与原始提示词以 FeatureIcons 的 manifest 为准，不在 Skill 中复制整份清单。`Catalog.All` 中不同格式共用同类 PNG；仍由 `FeatureIcon` 根据 `Label` 绘制格式标签。`clip-list` 用在转换窗口的批量剪辑入口，其他类型用于功能目录及对应窗口。

工具栏的开始、停止、删除等基础控制有独立含义。已有图标与含义不符时不要为覆盖率硬套；用户要求新素材时再按相同风格补充。
