# JoyTag

- Upstream: https://github.com/fpgaminer/joytag
- Model files: https://huggingface.co/fancyfeast/joytag
- License: Apache-2.0. Downloaded on demand; weights are not bundled.
- Immutable revision: `6b7f16331a6ccf0fdce37d5a9564715f6e772b22`
- `model.onnx`: 366116154 bytes, SHA-256 `f85b7130e6e549b5b0822537007b7482e8c4c8e754c8d9a5bee08e27050e1097`
- `top_tags.txt`: 76752 bytes, SHA-256 `32b1963a234af848643b2bbf47d8eff1f1c7889406810c57b980f41b2b9e01d0`

The ONNX output contains raw logits for 5813 independent tags. Apply sigmoid rather than softmax. Input: RGB, centered white square padding, 448×448, CLIP mean/std, NCHW float32. AvaMedia uses its image decoder for orientation and Skia resampling; scores may differ from the upstream PIL example.
