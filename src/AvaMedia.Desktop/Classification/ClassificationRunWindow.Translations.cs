// Run C → run B: append these en-US entries to Localization/Strings.en-US.json if absent.
// The required /workspace/aiprog/requests-C.md cannot be written: read-only filesystem.
// This handoff remains inside run C's permitted ClassificationRunWindow*.cs ownership.
/*
{
  "从任务列表「查看任务」打开 · 关闭窗口后任务继续运行": "Opened from View task · Processing continues after closing this window",
  "准备模型": "Prepare models",
  "逐个文件分析": "Analyze each file",
  "完成汇总": "Summarize results",
  "按添加顺序 · 每个文件依次完成下列步骤": "In input order · Each file follows the steps below",
  "首批服装区域完成后开始 · 之后每 8 个文件重新归组": "Starts after the first outfit results · Regroups every 8 files thereafter",
  "完成 / 待确认 / 失败计数，转入结果页": "Count completed, review and failed files, then show results",
  "未启用服装归组": "Outfit grouping is off",
  "仅按文件信息判定": "Classify using file information only",
  "第 2 步 · 当前文件": "Step 2 · Current file",
  "第 3 步 · 分组正在形成": "Step 3 · Groups taking shape",
  "第 {0} 次": "Pass {0}",
  "第 {0} / {1} 个": "File {0} / {1}",
  "第 {0} / {1} 帧": "Frame {0} / {1}",
  "读取画面": "Read frames",
  "JoyTag 标签": "JoyTag tags",
  "规则判定": "Evaluate rules",
  "{0} 组规则": "{0} rule groups",
  "采样 {0} 帧": "Sample {0} frames",
  "采样画面 · {0} 帧": "Sampled frames · {0} frames",
  "文件流 · 按处理顺序": "File flow · Processing order",
  "规则分类结果": "Rule classification results",
  "等待文件分析": "Waiting for file analysis",
  "等待分类结果": "Waiting for classification results",
  "等待服装区域识别": "Waiting for outfit recognition",
  "等待采样画面": "Waiting for sampled frames",
  "等待开始": "Waiting to start",
  "正在停止": "Stopping",
  "已跳过": "Skipped"
}
Backend observation limitation: no persisted total model-preparation duration exists.
Run C displays only the available real activity-node timing, leaving unavailable values blank.
Outfit frame counters are read from the current Job.ProgressDetail service format.
*/
