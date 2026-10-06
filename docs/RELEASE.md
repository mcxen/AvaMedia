# 自动发布

CI 在 main 推送和 Pull Request 时执行完整 Windows 验证。Release 工作流接收 `vMAJOR.MINOR.PATCH` tag，依次执行：

1. Windows 全部测试，包括实际媒体输出、消融、编辑器、皮肤、设置和接口。
2. 发布包含 .NET 运行时的 Windows x64 ZIP；以固定版本 Inno Setup 6.4.3 生成每用户安装程序，实际验证安装、版本、原生启动、许可证和卸载。
3. 在 Apple Silicon / Intel runner 分别验证接口和皮肤，构建应用 ZIP、PKG 与 DMG，并启动发布后的客户端生成原生截图。
4. 检查两个 Mac ZIP 的 Mach-O 架构和 Unix 执行权限，生成对应源码 ZIP、SHA256SUMS.txt，自动创建 GitHub Release 并上传。

必要步骤失败时不发布 Release。构建 job 使用 `contents: read`，上传 job 单独获得 `contents: write` 和 GitHub 自带的 `GITHUB_TOKEN`；Actions 固定到核对过的 SHA。同一个 tag 可手动重跑，成品会重新上传。后续版本使用新 tag，不移动已发布 tag。

```sh
git tag -a v1.0.5 -m "AvaMedia 1.0.5"
git push origin main
git push origin v1.0.5
```

版本统一传入 MSBuild、安装器、Info.plist 和文件名。源码默认版本位于 `Directory.Build.props`。

```powershell
./scripts/Publish.ps1 -Runtime win-x64 -Version 1.0.5
./scripts/Package-Windows.ps1 -Version 1.0.5
./scripts/Verify-WindowsInstaller.ps1 -Version 1.0.5
```

macOS 上发布相应架构后运行 `scripts/Package-Mac.ps1`：PKG 安装至 `/Applications`，DMG 提供 Applications 快捷方式。应用采用 ad-hoc 签名，没有 Developer ID 签名和公证；用户设备的录制权限与音频设备仍需验收。

新构建内置官方 yt-dlp 2026.08.19 和 QuickJS-NG 0.17.0，固定版本、核对 SHA256、保留许可证与来源清单。FFmpeg / FFprobe 继续作为独立媒体引擎。Windows 开始菜单提供工具安装入口，安装至应用 `tools` 目录；macOS 使用 Homebrew 或设置外部路径。依赖和安装器许可保存在 `licenses/`。

参考：[GitHub 工作流语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)、[Inno 编译参数](https://jrsoftware.org/ishelp/topic_compilercmdline.htm)、[Inno Setup 6.4.3 许可](https://github.com/jrsoftware/issrc/blob/is-6_4_3/license.txt)。
