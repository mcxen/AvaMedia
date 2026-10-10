# Mac OS 9 / Platinum 独立功能图标

本系列为当前 AvaMedia 的 Mac OS 9 皮肤制作，属于独立的原创图案。沿用功能含义，采用像素阶梯轮廓、近黑描边、灰银色框体、蓝色画面、绿色箭头或音符，齿轮和加号为金橙，剪刀柄和录制圆点为红色。

先查看 `docs/assets/feature-icons-macos9-preview.png`，以及 `src/AvaMedia.Desktop/Assets/FeatureIcons/macos9/rotate.png`。后者定义本系列的轮廓、比例、高光和留白。所有实际提示词保存在同目录的 `manifest.json`，补充图标时复用最接近功能的提示词。v2 图标可作符号含义参考，但不用于决定光滑曲线风格。

按约 48 × 48 的逻辑像素网格构图，使用方形阶梯边缘、方形胶片孔、少量大片明暗区域和简洁白色边缘高光。随包 PNG 使用 `scripts/Optimize-ImageAssets.py` 缩至 256 × 256 并优化编码，保留 alpha；原图尺寸和哈希记录在 manifest 的 `optimization`。不做调色板量化、锐化或重画。检查实际控件缩放后的 48/64 像素效果；不能仅根据高分辨率原图判断可读性。

主体约占画布 80%，四周透明，信息 `i` 以外不生成文字；文件类型标记仍由程序绘制。不要生成 Apple 标识、复制系统原生图标、使用现代圆角应用底板、厚重投影或照片材质。录制和播放器可使用简洁灰色条纹标题栏，但仍是一个独立功能图标。

## 保存和切换

- 素材：`src/AvaMedia.Desktop/Assets/FeatureIcons/macos9/{kind}.png`。
- 元数据：同目录 `manifest.json`，记录完整提示词、参考文件、原图尺寸、SHA-256 和内置生成模式。
- `FeatureIconAssets` 按系列分别缓存，缺失素材可回退 v2，但交付整套时必须验证所有类型均有专属素材。
- `FeatureIcon.ActualThemeVariant == Skin.MacOS9` 时使用该系列；切回 Light / Dark 使用 v2。新增类型须同时补齐两个系列。
- 用 `Check-IconAssets.ps1` 检查透明边角、完整覆盖和哈希，用 IconPreview 查看灰色和深色背景，用 SkinTests 验证逐项实时切换与子窗口继承。

生成时使用内置 imagegen，每个独立图标一次调用，启用真实透明背景。图像修订仍交给图像生成工具；复制原图和界面缩放不改变生成主体。
