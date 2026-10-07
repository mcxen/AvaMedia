# TS 视频

视频输入名单统一接受 `.ts`、`.mts`、`.m2ts`、`.m2t`，大小写不敏感。拖入主窗口按视频显示工具；文件夹扫描、WiFi 接收后的视频入口、批量工具与播放器复用同一名单。macOS 发布的媒体类型声明和 Windows 播放器注册也从该名单生成。

格式转换、视频合并 / 混流、视频流提取、重新封装、快速剪辑、视频压缩、批量裁剪及旋转均可选 TS 输出。TS 输入可转为 MP4、MKV 等已有格式；提取音频和导出帧使用现有媒体管线。Fast Copy 可保留 TS 源容器和编码，剪辑边界受关键帧限制；裁剪与画面旋转需要重新编码。

TS 自动软件编码选择 H.264（libx264）和 AAC。视频配置提供 H.264、HEVC、MPEG-2、MPEG-4 及兼容的 GPU 编码器；音频提供 AAC、MP2、MP3、AC3、EAC3，支持流复制及独立多音轨。普通导出和保留原属性的导出均显式选择 `mpegts` 封装器。H.264 / HEVC 码流封装转换交给 FFmpeg 的封装器处理，不重复追加位流滤镜。

压缩的质量、码率、百分比和目标体积模式均支持 TS；按体积计算及预计输出使用相同的 6% 封装预算，实际结果由编码内容和封装决定。

实现依据：[FFmpeg MPEG-TS 封装源码](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mpegtsenc.c)、[FFmpeg 位流滤镜文档](https://ffmpeg.org/ffmpeg-bitstream-filters.html)。本次只执行受影响项目编译与必要静态检查，未生成媒体、运行播放或功能回归。
