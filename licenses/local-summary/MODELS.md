# Local video summary models

- Qwen3 1.7B: Qwen team, Alibaba Cloud. Original model: https://huggingface.co/Qwen/Qwen3-1.7B. GGUF conversion distributed by ggml-org at https://huggingface.co/ggml-org/Qwen3-1.7B-GGUF, revision `daeb8e2d528a760970442092f6bf1e55c3b659eb`.
- SmolVLM2 500M Video Instruct: Hugging Face Smol Models Research. Original model: https://huggingface.co/HuggingFaceTB/SmolVLM2-500M-Video-Instruct. GGUF and quantized vision projector distributed by ggml-org at https://huggingface.co/ggml-org/SmolVLM2-500M-Video-Instruct-GGUF, revision `ccd7aae53bcb1997355c2f094959e72b3642ce17`.
- Both models are distributed under Apache-2.0; preserve `Apache-2.0.txt` and these attributions. The upstream weights are quantized conversions; AvaMedia does not train or fine-tune them. Weights are downloaded from upstream CDN on demand and are not included in the application package.
- llama.cpp b11476: Georgi Gerganov and contributors, MIT. Preserve `llama-cpp-MIT.txt`. Download archives and extracted runtime notices remain in the user's local `AvaMedia/models/summary-runtime/` cache; the runtime is not included in the application package.
- Whisper Small / Base / Tiny: OpenAI and whisper.cpp contributors, MIT. Existing licenses are in `licenses/speech/`.

Exact model files, byte sizes, immutable download revisions and SHA256 digests are recorded in `src/AvaMedia.Core/Assets/SummaryModels.json`, embedded as a small metadata resource. The release package contains only small auxiliary resources; models or inference tools over 10 MB are fetched on demand.
