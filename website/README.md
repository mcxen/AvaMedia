# AvaMedia 宣传页

独立的中英文静态宣传页，使用 HTML、CSS、JavaScript 和 Vite。不依赖桌面客户端构建。

```sh
cd website
npm ci
npm run dev
```

开发地址为 `http://127.0.0.1:5173`。生产构建与预览：

```sh
npm run build
npm run preview
```

预览地址为 `http://127.0.0.1:4173`。部署时上传 `dist/` 到静态主机即可；资源路径为相对路径，可用于域名根目录或子目录。这里没有配置公开域名或自动部署。

页面默认中文，右上角切换英文；也支持 `?lang=zh` 与 `?lang=en`，选择保存到浏览器。无需 JavaScript 也能阅读中文内容并打开下载页。

## 内容与素材

- 功能范围依据仓库中英文 README 和 `docs/FEATURES.md`，不宣传未接入的 LaMa 修复推理等功能。
- `scripts/prepare-assets.mjs` 在开发、构建前复制仓库现有真实截图及应用图标到生成目录 `public/media/`。不重复维护图片，也不抓取新截图。
- 截图来源见 [`../docs/assets/screenshots/README.md`](../docs/assets/screenshots/README.md)。编辑器示例为 NASA 公共领域照片，来源见 [`../tests/AvaMedia.BatchRotateTests/Fixtures/README.md`](../tests/AvaMedia.BatchRotateTests/Fixtures/README.md)。播放器配图是测试图案。
- 下载入口默认指向 GitHub 最新 Release。JavaScript 从公开 Release API 解析 Windows 安装版、便携 ZIP 和 macOS DMG；只接受本仓库已上传的安装包 URL。请求超时、限流或离线时保留发布页入口，不绑定旧版本号。
- 无分析脚本、外部字体或视频依赖。页面唯一的外部数据请求是公开 GitHub Release 元数据。

## 设计参考

参考 Prompt Motion 的 [Photo print app launch film](https://prompt-motion.com/twoclipping-221cab)：暖白画布、黑色产品界面、文件到输出的连续叙事。将这个方向用于网页首屏、截图展示和处理流程；没有复制参考视频、文字或素材。交互使用原生 tab / dialog / details，支持键盘导航与减少动态效果偏好。

按根目录 `AGENTS.md`，本页默认只做相关生产构建与静态检查，不自行运行浏览器功能回归、截图或性能测试。

原创网页代码沿用仓库 AGPL-3.0-only 许可证。
