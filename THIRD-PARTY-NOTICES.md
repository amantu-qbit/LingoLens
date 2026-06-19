# Third-Party Notices

LingoLens is licensed under the [MIT License](LICENSE). It downloads and uses the following
third-party models at runtime; each retains its own license.

## Models

| Model | Purpose | License | Notes |
|---|---|---|---|
| PP-OCRv5 / RapidOCR | Chinese OCR | Apache-2.0 | © PaddlePaddle / RapidAI |
| Qwen3-4B | Translation (quality) | Apache-2.0 | © Alibaba Qwen team; GGUF quant by Unsloth |
| Opus-MT zh→en (Helsinki-NLP) | Translation (fallback) | **CC-BY-4.0** | Attribution required — see below |

### Opus-MT attribution (CC-BY-4.0)

The `opus-mt-zh-en` translation model is provided by the **Language Technology Research Group at
the University of Helsinki** (OPUS-MT; Tiedemann & Thottingal), licensed under
[CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/). ONNX export hosted by Xenova on the
Hugging Face Hub.

Models under non-commercial terms (e.g. **NLLB**, CC-BY-NC) and custom terms (e.g. **Gemma**) are
intentionally **not** used, so LingoLens can be used and redistributed commercially.

## Libraries

- **ONNX Runtime** (MIT) — model inference
- **LLamaSharp** / **llama.cpp** (MIT) — GGUF LLM inference
- **Vortice.Windows** (MIT) — Direct3D / DXGI / Direct2D / DirectComposition bindings
- **CommunityToolkit.Mvvm** (MIT) — MVVM
- **Microsoft.Extensions.*** (MIT) — hosting, DI, configuration, logging
