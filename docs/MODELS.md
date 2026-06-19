# Models

LingoLens downloads models on first use into `%LOCALAPPDATA%\LingoLens\models\<bundle>\`.
All sources are permissively licensed (no NLLB / no Gemma). URLs below were live-verified.

> SHA-256 is pinned where the source publishes one; small text/tokenizer blobs without a
> published hash are downloaded without hash verification (a warning is logged). Consider
> pinning your own hashes after first download for supply-chain integrity.

## `ppocrv5-mobile` — OCR (PP-OCRv5 mobile) · Apache-2.0

Source: **RapidAI/RapidOCR** (canonical `default_models.yaml`, tag `v3.8.0`). Listed via the
ModelScope mirror (verified resolving); the same files exist on Hugging Face at
`huggingface.co/RapidAI/RapidOCR/resolve/main/<same-path>`.

| File | Size | SHA-256 |
|---|---|---|
| `ch_PP-OCRv5_det_mobile.onnx` | 4,819,576 | `4d97c44a…6ae78ae` |
| `ch_PP-OCRv5_rec_mobile.onnx` | 16,631,306 | `5825fc7e…9b1742c5` |
| `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` | 1,018,508 | `54379ae5…cc82df7` |
| `ppocrv5_dict.txt` | 74,012 | *(none published)* |

Base path: `https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/onnx/PP-OCRv5/{det,rec,cls}/…`
(dict: `…/resolve/v3.8.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt`).

Pure-HF det+rec alternative (independent conversion, different bytes/sha): `ilaylow/PP_OCRv5_mobile_onnx`
(no cls/dict — get those from RapidAI/PaddleOCR).

## `opus-mt-zh-en` — translation fallback (Marian/OPUS-MT zh→en) · CC-BY-4.0

Source: **Xenova/opus-mt-zh-en** (ready-made ONNX export of `Helsinki-NLP/opus-mt-zh-en`).
**Attribution required (CC-BY-4.0):** credit Helsinki-NLP / OPUS-MT in the app's About/credits.

| File | Size | SHA-256 |
|---|---|---|
| `onnx/encoder_model.onnx` | 209,938,220 | `555761f7…99f6cd72` |
| `onnx/decoder_model_merged.onnx` | 235,839,236 | `f1851afa…3ba85aff` |
| `source.spm` | 804,677 | *(none)* |
| `target.spm` | 806,530 | *(none)* |
| `vocab.json` | 1,747,906 | *(none)* |
| `config.json` | — | *(none)* — used for decoder_start/eos/pad ids |

Base: `https://huggingface.co/Xenova/opus-mt-zh-en/resolve/main/…`.
Smaller footprint: `onnx/encoder_model_int8.onnx` (~53 MB) / `onnx/decoder_model_merged_int8.onnx` (~60 MB).

## `qwen3-4b-int4` — translation quality (Qwen3-4B GGUF) · Apache-2.0

Source: **unsloth/Qwen3-4B-GGUF** (`Q4_K_M`, ~2.5 GB, single file for llama.cpp/LLamaSharp).
Interchangeable second source: `bartowski/Qwen_Qwen3-4B-GGUF`. Requires a LLamaSharp backend
(see `docs/RUNNING.md`).

| File | Size | SHA-256 |
|---|---|---|
| `Qwen3-4B-Q4_K_M.gguf` | 2,497,281,312 | `f6f85177…2195813a` |

URL: `https://huggingface.co/unsloth/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf`.

## License summary

| Model | License | Commercial | Notes |
|---|---|---|---|
| PP-OCRv5 (RapidOCR) | Apache-2.0 | ✅ | — |
| Opus-MT zh-en | CC-BY-4.0 | ✅ | **attribution required** |
| Qwen3-4B | Apache-2.0 | ✅ | — |

Deliberately excluded: **NLLB** (CC-BY-NC, non-commercial) and **Gemma**-licensed models (custom terms).
