# 真人标签端到端验证

2026-10-09 在 macOS ARM64 上按用户要求执行真实素材验证。使用生产 `MediaTagService`、`MediaEngine.Execute` 和原生 `MediaAiWindow`，未替换模型输出或抽帧逻辑。成人素材仅保存在本机忽略目录，报告使用样本编号。

## 实现与模型

- 真人模式默认开启，中文词库包含 354 个候选；保留完整 JoyTag 原始分数，显示人物、姿态、服饰与成人标签。
- JoyTag 用于细分标签；Marqo NSFW ONNX 用于独立二分类。分类阈值为 0.50，与标签合格线 0.40 分开。
- Marqo 使用 [ICIJ 转换版本](https://huggingface.co/ICIJ/nsfw-image-detection-384-onnx/tree/4f63fac119fb24a9d98393e57549b1427530ca52)，22,450,955 字节，SHA256 `50256dde930a4ceeb8953246ba7345158e02cc0a201a173402ab3e6e3a76f40a`。
- 模型通过现有模型管理下载并校验 SHA256。姿态与成人标签按视频采样峰值判定；“姿态 / 体位”筛选支持中文标签及曲线。

## 实际推理

三张照片和一个真实视频，视频等距采样八帧。照片姿态经过人工查看；NSFW 预期来自人工确认的普通与成人样本对照。以下为 CPU 生产路径的结果。

| 样本 | 人工标注 | NSFW 峰值 | NSFW 判定 | 姿态对照 |
| --- | --- | ---: | --- | --- |
| S01 | 普通真人照片 | 0.048610 | 未达阈值 | 站立 0.431679，命中 |
| I08 | 成人真人照片 | 0.928426 | 疑似 NSFW | 坐着 0.560731，命中 |
| I12 | 成人真人照片 | 0.722147 | 疑似 NSFW | 站立 0.727278，命中 |
| V01 | 成人真人视频 | 0.952136 | 疑似 NSFW | 八帧，姿态仅记录输出 |

S01 来自 [MMPose 的 COCO 测试照片](https://github.com/open-mmlab/mmpose/blob/main/tests/data/coco/000000000785.jpg)。V01 的 NSFW 采样平均为 0.850367。四个样本的二分类与人工预期一致；三个已标注照片姿态都达到 0.40。

## 输出与原生工作台

端到端程序确认：模型下载与哈希校验、每帧 5813 项标签分数、双模型采样时间一致、有限分数、中文结果、真人标签过滤、独立 NSFW 阈值、同目录 TXT 更新替换、JSON 序列化往返及生产任务报告输出全部通过。原文件字节哈希、大小与修改时间一致，报告写入无临时文件残留。

原生工作台实际完成四个文件的分析，结果分别显示 8、16、12、36 个标签；后端报告为 `Core ML / CPU`。确认真人开关、中文词库及“四肢撑地”搜索，普通照片显示“未检出”，成人样本显示“疑似 NSFW”。原生“生成同目录 TXT”已生成四份报告，文件中包含独立分类模型、后端和逐采样分数。

原生 JSON 导出验证发现并修复了后台序列化读取 UI 控件导致的线程错误。修复后实际导出的 JSON 为 290,104 字节，包含普通真人照片的全部 5813 项原始分数、中文标签和独立 NSFW 分类。

最终原生补验确认：点击 NSFW 徽标展开“识别详情”，显示 Marqo 峰值与平均 0.049、分类阈值 0.50、CPU 后端和采样时间；“姿态 / 体位”筛选显示站立 0.432 及对应曲线。切换筛选会重建详情，分类依据仍保持可读。

相关 Core、Desktop 和验收项目的 Release 编译通过，零警告、零错误。合入并行提交后，共享工作区尚未完成的目录分类界面一度导致编译失败；最终从已提交源码快照构建，保留并行未提交内容。测试工具的 `real-people` 模式允许用本机清单重现，`real-ui` 模式打开生产工作台。清单对象字段为 `Id`、`Path`、`ExpectedNsfw` 和可选 `ExpectedPose`，至少包含普通真人、成人真人及视频。

```sh
.tools/dotnet/dotnet build tests/AvaMedia.AiTests/AvaMedia.AiTests.csproj -c Release -r osx-arm64 --artifacts-path artifacts/real-people-build
.tools/dotnet/dotnet artifacts/real-people-build/bin/AvaMedia.AiTests/release_osx-arm64/AvaMedia.AiTests.dll real-people artifacts/real-people-e2e-final /absolute/path/inventory.json
```

最终本机结果：`artifacts/real-people-delivery-e2e/acceptance.json`；完整结果：`artifacts/real-people-delivery-e2e/results.json`；原生导出：`artifacts/real-people-ui-final/media/ui-final-results.json`。这些文件和素材不提交到仓库。

## 验证范围

本次验证的是完整识别和报告流程，以及小样本分类、普通姿态命中；没有逐体位标注的视频基准，不能据此宣称完整体位识别准确率。姿态来自图像标签，未输出人体骨架坐标。视频八帧采样不能排除帧间事件，模型分数也不等于经过本地校准的发生概率。Windows 安装包和发布资产不在这次本机验证范围内。
