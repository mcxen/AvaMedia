# Git 与验证

`main` 保存集成代码，`v1.0.2-verified` 固定已验证的 Windows 1.0.2 源码基线（369 项检查、52 个转换入口）。标签不随后续开发移动。对应包的 SHA256 记录在 `docs/releases/1.0.2-verified.json`。

源码、测试、说明和许可证纳入 Git；`.tools/`、`bin/`、`obj/`、`artifacts/` 和本地参考截图不纳入 Git。源文件统一 LF，Windows 启动脚本使用 CRLF。当前仓库使用已有的 Git 作者配置。

## 日常修改

1. 用 `git status --short` 与 `git diff` 检查修改，按功能提交明确的文件路径。
2. 运行 `./scripts/Verify.ps1`。它构建解决方案并执行全部 10 个测试套件，输出日志、源码提交号和工作区是否存在未提交改动。
3. 用 `git diff --cached` 复核内容，再提交。功能实现、行为修复、格式整理和验证工具分别提交。
4. 发布标签指向通过验收的提交；运行结果与发布包保存在 `artifacts/`，通过提交号及哈希关联。

单独复现消融：`./scripts/Verify.ps1 -Suite Ablation`。测试说明和已测结果见 [ABLATION.md](ABLATION.md)。

## 协作与备份

并行修改时只暂存自己的明确路径，保留其他未提交改动；避免对共享工作区执行 `reset --hard`、`clean` 或强制覆盖。需要长时间独立开发时使用分支与独立 worktree。

当前仅管理本地历史，尚未配置远程仓库。提交和标签的离线备份可用：

```powershell
git bundle create artifacts/AvaMedia-history.bundle --all
git bundle verify artifacts/AvaMedia-history.bundle
```

恢复到新目录：`git clone artifacts/AvaMedia-history.bundle AvaMedia-restored`。待指定真实远程仓库后再添加 remote 和推送；不生成虚构仓库地址。
