# Running LingoLens (developers & testers)

## Prerequisites

- **Windows 11** (best) or **Windows 10 1903+**.
- **.NET 10 SDK** (`dotnet --version` ≥ 10.0).
- **Segoe Fluent Icons** font (ships with Windows 11; on Win10 the app falls back to Segoe MDL2 Assets).
- For Chinese OCR via the zero-download path: install the **Chinese (Simplified/Traditional) language features** (Settings → Time & language → Language → add Chinese → Language options → install the optional *Optical character recognition* feature).
- Optional GPU: any DX12 GPU (NVIDIA/AMD/Intel) is auto-used via DirectML; NVIDIA can use CUDA/TensorRT if those ONNX Runtime providers are present.

## Build & test

```bash
dotnet restore LingoLens.slnx
dotnet build  LingoLens.slnx -c Debug
dotnet test   LingoLens.slnx
```

## Run

```bash
dotnet run --project src/LingoLens.App -c Debug
```

The floating control bar appears bottom-center. Click **Translate**, pick a target
(a window, a monitor, or drag a region), and translated English is drawn over the source.

## Models (first-run download)

Models are **not** committed; they download on first use into
`%LOCALAPPDATA%\LingoLens\models\<bundle>\`. Bundles (see `OcrModelBundles` /
the Translation model repository):

| Bundle | Purpose | Tier | License |
|---|---|---|---|
| `ppocrv5-mobile` | PP-OCRv5 detect + recognize + dict (ONNX) | Balanced/Quality | Apache-2.0 |
| `opus-mt-zh-en` | Opus-MT zh→en (ONNX + SentencePiece) | Light/CPU | Apache-2.0/MIT |
| `qwen3-4b-int4` | Qwen3-4B GGUF (quality translator) | Quality | Apache-2.0 |

> The model manifest URLs/hashes are finalized in `docs/MODELS.md`. Until a bundle is
> installed, its engine reports `IsReady = false` and the pipeline falls back:
> **Windows.Media.Ocr** (no download) for OCR, and **Opus-MT** for translation.

### Baseline that runs with zero extra setup
- **OCR:** Windows.Media.Ocr (needs the zh language OCR feature installed).
- **Translation:** Opus-MT once `opus-mt-zh-en` is present.

### Enabling the Qwen3 quality translator
Qwen3 runs through **LLamaSharp**, which needs a native backend. Add the backend that
matches the target hardware to `LingoLens.App`:
- NVIDIA: `LLamaSharp.Backend.Cuda12`
- Any GPU (portable): `LLamaSharp.Backend.Vulkan`
- CPU-only: `LLamaSharp.Backend.Cpu`

(These are intentionally not referenced by default to keep the dev build light and the
installer small; the installer wires the right backend per target — see packaging, P3.)

## Verification checklist (requires real hardware — can't be tested in CI)

- [ ] Control HUD renders with Mica/dark styling; drag, minimize, close work.
- [ ] Target picker lists open windows + monitors; region selector draws a box.
- [ ] Overlay window is **click-through** (clicks pass to the app beneath) and **topmost**.
- [ ] Capturing a **specific window** works even when it is partially occluded (WGC).
- [ ] Chinese text on screen is detected and replaced with English in place.
- [ ] Overlay **follows** the target window as it moves/resizes; correct on multi-monitor + mixed DPI.
- [ ] Refresh-rate-paced render is smooth on 60/120/144 Hz.
- [ ] Latency readout shows < ~150 ms for fresh text; cached strings are instant.
- [ ] DRM/protected windows show a "protected content" state (no crash, no bypass).
- [ ] Device picker switches compute device; tier updates.

## Known TODOs carried from implementation (see code `TODO(verify-on-hardware)`)

- 24H2-only WGC properties (`MinUpdateInterval`, `IsBorderRequired`, `DirtyRegionMode`,
  `Direct3D11CaptureFrame.DirtyRegions`) are set via reflection guarded by `ApiInformation`;
  validate on a 24H2 SDK and replace with typed calls.
- DXGI duplication dirty/move-rect parsing + `ACCESS_LOST` recovery on multi-GPU/monitor.
- PP-OCRv5 DBNet box extraction + CTC decode accuracy on real Chinese UI/screens.
- Region selector DIP→pixel mapping across mixed-DPI monitors.
- Mixed DirectML vs CUDA/TensorRT runtime selection on NVIDIA (default build ships DirectML).
