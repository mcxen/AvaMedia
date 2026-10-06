独立实现的 Avalonia + C# 客户端，包含多片段编辑与导出、共享裁剪、本地方向识别、Light / Dark / Mac OS 9 三套皮肤，以及统一的编辑器组件和精确帧时间。

普通选项补齐源目录输出、设置名称、完成后打开目录与可取消的关机倒计时、操作/完成/错误提示音、系统菜单、托盘和正式版本检查。转换与剪辑、批量裁剪和旋转共用默认配置；完成后动作只针对本次任务。Windows 与 macOS 各自接入系统入口，Mac 原生运行仍需真机验收。设置说明见 [普通选项](GENERAL-OPTIONS.md)。

Windows 外部工具安装改用共享 DLL 构建，FFmpeg / FFprobe 运行文件约 153.71 MiB，相比原静态组合减少 41.7%；不再安装未使用的 ffplay，并清理临时下载与解压文件。编码器、解码器、滤镜、格式、协议与设备列表一致，完整 18 套客户端测试通过。

成品包括 Windows x64 安装程序与便携 ZIP，macOS Apple Silicon / Intel 的应用 ZIP、PKG 和 DMG，对应源码及 SHA256 清单。客户端包含 .NET 运行时；FFmpeg / FFprobe / yt-dlp 需通过附带脚本或设置独立配置。

原创代码采用 AGPL-3.0-only，安装包保留第三方许可证。macOS 应用采用 ad-hoc 签名，尚未进行 Developer ID 签名和公证。
