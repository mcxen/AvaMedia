# 人脸方向测试素材

`astronaut.png` 来自 scikit-image 0.25.2 的测试素材，原图为 NASA 提供的公共领域照片。

- 来源：https://github.com/scikit-image/scikit-image/blob/v0.25.2/skimage/data/astronaut.png
- 公共领域声明：https://scikit-image.org/docs/stable/api/skimage.data.html#skimage.data.astronaut
- SHA256：`88431cd9653ccd539741b555fb0a46b61558b301d4110412b5bc28b5e3ea6cb5`

测试通过 FFmpeg 生成不同方向及旋转元数据的视频，再检查检测结果、最终像素及取消行为。该素材仅用于方向功能回归，不用于身份识别，也不能代表真实视频数据集的准确率。
