# Person detection models

These unmodified ONNX models are downloaded separately from OpenCV Zoo, revision `d4938dfc9d4ec5d098bfa33e98b3f3345a236586`. The OpenCV model directories license these files under Apache-2.0. Preserve the enclosed LICENSE.

- YOLOX: Megvii YOLOX authors and OpenCV contributors. Source: https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox . `object_detection_yolox_2022nov.onnx`, 35,858,002 bytes, SHA256 `c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063`.
- NanoDet: RangiLyu / NanoDet authors and OpenCV contributors. Source: https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_nanodet . `object_detection_nanodet_2022nov_int8bq.onnx`, 1,123,958 bytes, SHA256 `8a2c877cc6f09e7dfac7a9066e33ee5ae68de530b3b994f6ee9125cff6e34d3f`.
- MediaPipe person detector: Google MediaPipe authors, PINTO model conversion contributors and OpenCV contributors. Source: https://github.com/opencv/opencv_zoo/tree/main/models/person_detection_mediapipe . `person_detection_mediapipe_2023mar.onnx`, 11,990,159 bytes, SHA256 `47fd5599d6fa17608f03e0eb0ae230baa6e597d7e8a2c8199fe00abea55a701f`.

Before inference, AvaMedia canonicalizes the pinned NanoDet graph's four empty Resize optional inputs. Source downloads and weights are unchanged. Inference caches use the canonical graph hash. AvaMedia's inference adapters use the corresponding input normalization and output layout. No Python runtime or upstream demonstration application is shipped. The fusion policy, video interval analysis and editing workflow are AvaMedia code.
