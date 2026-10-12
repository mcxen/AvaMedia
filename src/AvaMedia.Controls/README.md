# AvaMedia.Controls

独立的 Avalonia 11.3.22 通用组件库，不引用 AvaMedia 的业务、桌面或媒体项目。保留宿主的 Themes.Simple；现代组件通过角色类名与具名 ControlTheme 接入，经典皮肤保留宿主原生控件模板。所有颜色、尺寸和圆角取自主题资源。

结构可独立打包：`dotnet pack AvaMedia.Controls.csproj`。项目自身声明依赖和包元数据，离开本仓库也能构建。许可证沿用项目的 AGPL-3.0-only。

实现自行编写；Devolutions.AvaloniaTheme.MacOS 仅作为评估参考，未复制其源码。最后兼容 Avalonia 11 的稳定版本是 2026.6.16；不引入 Avalonia 12 的依赖或整体主题。
