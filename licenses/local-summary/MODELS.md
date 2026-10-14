# Local video summary models

- Huihui Qwen3.5 4B abliterated: Qwen team, Alibaba Cloud; refusal-reduced variant by huihui-ai. Original model: https://huggingface.co/Qwen/Qwen3.5-4B. Variant: https://huggingface.co/huihui-ai/Huihui-Qwen3.5-4B-abliterated. Q4_K_M GGUF and f16 vision projector distributed by mradermacher at https://huggingface.co/mradermacher/Huihui-Qwen3.5-4B-abliterated-GGUF, revision `4a5daa6fbefca5fe822dc65fcb95cc4576fa9720`. The same model handles visual analysis and text summaries.
- The model is distributed under Apache-2.0; preserve `Apache-2.0.txt` and these attributions. The upstream weights are quantized conversions; AvaMedia does not train or fine-tune them. Weights are downloaded from upstream CDN on demand and are not included in the application package.
- llama.cpp b11476: Georgi Gerganov and contributors, MIT. Preserve `llama-cpp-MIT.txt`. Download archives and extracted runtime notices remain in the user's local `AvaMedia/models/summary-runtime/` cache; the runtime is not included in the application package.
- Whisper Small / Base / Tiny: OpenAI and whisper.cpp contributors, MIT. Existing licenses are in `licenses/speech/`.

Exact model files, byte sizes, immutable download revisions and SHA256 digests are recorded in `src/AvaMedia.Core/Assets/SummaryModels.json`, embedded as a small metadata resource. The release package contains only small auxiliary resources; models or inference tools over 10 MB are fetched on demand.
