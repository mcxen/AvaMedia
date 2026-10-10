# Git 与验证

`main` 保存公开的 AGPL-3.0-only 集成代码，`v1.0.2-agpl` 固定首个公开版本。标签不随后续开发移动。历史 Windows 验收和包的 SHA256 记录在 `docs/releases/`。

源码、测试、说明和许可证纳入 Git；`.tools/`、`bin/`、`obj/`、`artifacts/` 和本地参考截图不纳入 Git。源文件统一 LF，Windows 启动脚本使用 CRLF。当前仓库使用已有的 Git 作者配置。

## 开发阶段约定

当前仍为开发版，以当前代码、配置结构和依赖为准。需要优化时直接替换旧实现，不为历史版本增加兼容层、配置迁移、旧组件保留或旧安装／依赖清理分支。版本标签和历史记录作为追溯资料保留，不作为兼容目标。

发布使用空输出目录；已有产物保留在各自目录，新构建不合并其它构建的文件。`scripts/Publish.ps1` 在开始编译前检查发布目录，目录非空时应指定新的 `-OutputDirectory`。macOS 应用组装目录使用相邻的 `发布目录-bundle`，也须为空。

## 日常修改

日常开发统一在 `main` 上继续，不因普通功能修改额外创建开发分支；仅在用户明确要求时创建分支。机器人自动创建的优化分支合入后清理。

1. 用 `git status --short` 与 `git diff` 检查修改，按功能提交明确的文件路径。
2. 按 `AGENTS.md` 完成相关项目编译、脚本语法及必要静态检查；默认不运行功能回归、消融或全量 `Verify.ps1`。仅在用户明确要求相应测试时执行。CI 在后台执行，不等待或持续轮询。
3. 用 `git diff --cached` 复核内容，再提交。功能实现、行为修复、格式整理和验证工具分别提交。
4. 日常提交照常推送；自上次已发布 tag 起，累计有效代码变更达到 **1200 行**并通过本地验收后，才创建并推送新版本标签。标签指向通过验收的提交；推送 `vMAJOR.MINOR.PATCH` 自动触发 [Release 工作流](RELEASE.md)，构建安装包、源码与校验清单并发布。运行结果与本地包保存在 `artifacts/`，通过提交号及哈希关联。

变更行数使用候选提交相对上次已发布 tag 的 `git diff --numstat`，累加新增行与删除行。只统计源码、界面模板、测试及构建/发布脚本；文档、许可证、资源文件和生成文件不计入，不为凑数制造改动。未达到 1200 行时继续正常提交和推送，保留当前版本 tag。

单独复现消融：`./scripts/Verify.ps1 -Suite Ablation`。测试说明和已测结果见 [ABLATION.md](acceptance/ABLATION.md)。

## 协作与备份

并行修改时只暂存自己的明确路径，保留其他未提交改动；避免对共享工作区执行 `reset --hard`、`clean` 或强制覆盖。需要隔离处理合并时可使用临时分离 HEAD 的 worktree，完成后清理，不额外创建开发分支。

公开仓库为 [mcxen/AvaMedia](https://github.com/mcxen/AvaMedia)，origin 使用 HTTPS。main 对应已验收代码，当前项目许可证为 AGPL-3.0-only。提交和标签的离线备份可用：

首个公开版本仍由 `v1.0.2-agpl` 固定。公开前的本地开发历史已接入 `main`，合入时保留当前代码树；原归档分支已清理，早期验收标签保留原提交。合并前的完整引用备份保存在本地 `artifacts/branch-backups/`；日常只推送 `main` 和明确选定的版本标签。

```powershell
git bundle create artifacts/AvaMedia-history.bundle --all
git bundle verify artifacts/AvaMedia-history.bundle
```

恢复到新目录：`git clone artifacts/AvaMedia-history.bundle AvaMedia-restored`。推送日常提交使用 `git push origin main`；验收标签单独明确推送，保留既有历史与记录。
