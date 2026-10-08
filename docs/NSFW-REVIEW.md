# NSFW 审核与反馈

开启 Beta 后，进入图片 / 视频 AI 标签，分析已有文件。每行显示模型建议、命中证据及分数，可以选择人工结论：未审核、NSFW、非 NSFW、不确定。

## 词库来源

| 词库 | 来源与许可证 | 接入方式 |
| --- | --- | --- |
| NSFW 审核标签 | [JoyTag](https://github.com/fpgaminer/joytag)，Apache-2.0；[固定词表](https://huggingface.co/fancyfeast/joytag/blob/6b7f16331a6ccf0fdce37d5a9564715f6e772b22/top_tags.txt) | 68 个真实标签，29 个风险候选、39 个上下文提示 |
| NudeNet 审核分类 | [NudeNet](https://github.com/notAI-tech/NudeNet/blob/6ccc81c6c305cccfd46d92b414f8a5c0a816574d/README.md)，AGPL-3.0 | 18 个源类别，中文映射，语义候选 |

来源、固定修订与许可证副本见 `licenses/nsfw-review/`。JoyTag 使用 Danbooru 风格的固定词表；新增中文分组和风险/提示策略由 AvaMedia 整理。NudeNet 分类不冒充 JoyTag 输出，没有捆绑 NudeNet 代码、权重或检测框。

[WD Tagger v3](https://huggingface.co/SmilingWolf/wd-vit-tagger-v3) 支持分级，但训练数据主要是 Danbooru 图像；本次不将它的独立分级头或词表假装成 JoyTag 能力。没有明确数据许可证的第三方标签树未纳入。

## 模型建议

- 风险候选达到用户阈值：疑似 NSFW，待人工审核。
- 只有衣着、体毛、道具、遮挡等提示命中：仅命中提示标签。
- 没有审核词库标签达到阈值：未检出风险标签。不能据此认定为安全。

图片用单图分数；视频用每个标签在采样画面中的最高分，并保留均值。视频只是有限采样，证据没有逐帧时间定位。分数和建议未经该素材库校准，不是统一 NSFW 概率，不判定年龄、同意情况或违法性。添加词库不改变推理权重；人工结论不会由模型自动填写。

## 人工审核与训练反馈

审核结论原子保存到当前用户数据目录的 `AvaMedia/nsfw-reviews.json`。重新分析同一路径、大小与修改时间的文件可恢复人工结论；源文件身份改变时不沿用旧结论。重命名后标签结果失效，需重新分析。审核记录损坏或写入失败会记录异常并提示，不通过覆盖损坏记录来恢复。

“导出标签 JSON”包含模型建议、证据、词库哈希及当前人工结论。“导出审核反馈 JSONL”只导出已有分析结果且人工选择了结论的样本，未审核记录不会导出；人工选择“不确定”会保持 `Uncertain`，不转成二元标签。

JSONL 每行是一条 `avamedia.nsfw-feedback.v1` 记录，含：

- 原文件路径、大小、修改时间与导出时计算的 `ContentSha256`。
- 模型文件大小、SHA-256、固定下载来源、审核词库 `DictionarySha256`。
- `HumanReview` 与审核时间，和独立的 `ModelAssessment`。
- 全部原始 `TagScores`，包含采样均值及峰值、实际后端与采样数量。

导出前后核对原文件身份；源文件已改变时拒绝该记录并要求重新分析。导出不包含图片或视频字节，不上传素材。数据可供离线评估、校准或人工反馈训练使用；AvaMedia 当前没有训练器，不会自动微调模型，也不把模型预测作为人工真值。下游二元训练应明确排除或单独处理 `Uncertain`。
