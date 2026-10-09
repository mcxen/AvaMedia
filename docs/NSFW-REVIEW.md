# NSFW 标签识别

开启 Beta 后，进入媒体 AI 标签，添加文件后点击“开始分析”。左侧选择文件，右侧按类别查看标签；疑似 NSFW 会在结果区显示。展开“识别详情”查看风险判断、命中证据和分数。

## 词库来源

| 词库 | 来源与许可证 | 接入方式 |
| --- | --- | --- |
| NSFW 识别标签 | [JoyTag](https://github.com/fpgaminer/joytag)，Apache-2.0；[固定词表](https://huggingface.co/fancyfeast/joytag/blob/6b7f16331a6ccf0fdce37d5a9564715f6e772b22/top_tags.txt) | 68 个真实标签，29 个风险候选、39 个上下文提示 |
| NudeNet 分类 | [NudeNet](https://github.com/notAI-tech/NudeNet/blob/6ccc81c6c305cccfd46d92b414f8a5c0a816574d/README.md)，AGPL-3.0 | 18 个源类别，中文映射，语义候选 |

来源、固定修订与许可证副本见 `licenses/nsfw-review/`。JoyTag 使用 Danbooru 风格的固定词表；新增中文分组和风险/提示策略由 AvaMedia 整理。NudeNet 分类不冒充 JoyTag 输出，没有捆绑 NudeNet 代码、权重或检测框。

[WD Tagger v3](https://huggingface.co/SmilingWolf/wd-vit-tagger-v3) 支持分级，但训练数据主要是 Danbooru 图像；本次不将它的独立分级头或词表假装成 JoyTag 能力。没有明确数据许可证的第三方标签树未纳入。

## 自动判断

- 风险候选达到用户阈值：疑似 NSFW。
- 只有衣着、体毛、道具、遮挡等提示命中：仅命中提示标签。
- 没有识别词库标签达到阈值：未检出风险标签。不能据此认定为安全。

图片用单图分数；视频用每个标签在采样画面中的最高分，并保留均值。视频只是有限采样，证据没有逐帧时间定位。分数和判断未经该素材库校准，不是统一 NSFW 概率，不判定年龄、同意情况或违法性。添加词库不改变推理权重。

## 标签导出

“导出标签 JSON”包含模型、阈值、原文件路径、实际后端、采样数量、达标标签，以及自动 NSFW 判断、命中证据和词库 `DictionarySha256`。导出不包含图片或视频字节，不上传素材。重命名后标签结果失效，需重新分析。
