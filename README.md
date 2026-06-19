<div align="center">

# LingoLens

**A transparent, on-device screen-translation overlay for Windows.**

Read Chinese-only apps and chats in English — instantly, privately, on any hardware.

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0a7bbb)
![.NET](https://img.shields.io/badge/.NET-10-512bd4)
![license](https://img.shields.io/badge/license-MIT-3fbf82)
![offline](https://img.shields.io/badge/100%25-on--device-3fbf82)

</div>

---

LingoLens floats a click-through overlay on top of any window, detects the on-screen
Chinese text, and draws an English translation **right where the original sits** — so a
Chinese-only program or a Telegram/Discord conversation simply *reads in English*. Everything
runs **on your machine**: no cloud, no per-character API bills, and nothing you see on screen
is ever uploaded.

## Table of contents

- [Why](#why)
- [Features](#features)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Install & run](#install--run)
- [First-run setup (models & language pack)](#first-run-setup-models--language-pack)
- [Using LingoLens](#using-lingolens)
- [Settings](#settings)
- [Configuration reference](#configuration-reference)
- [Performance & tuning](#performance--tuning)
- [Troubleshooting](#troubleshooting)
- [Project structure](#project-structure)
- [Support](#support)
- [Privacy](#privacy)
- [Credits & licenses](#credits--licenses)

## Why

- **Chinese-only software** you otherwise can't navigate.
- **Telegram / Discord** communities that chat in Chinese, where important details slip past you.
- You want **Google-Translate-class accuracy** without sending your screen to the cloud or paying an API.

## Features

- **Replace-in-place overlay** — English is drawn over the original on a frosted, auto-contrast
  backplate, auto-fit to the original's footprint, flicker-free with smooth fades.
- **Three capture modes** — a chosen **window** (works even when it's behind other windows),
  a **region** you drag out, or a whole **monitor**.
- **Nearly instant** — fresh text in ~35–85 ms; anything seen before is served from a translation
  memory in under 5 ms.
- **Runs on any hardware** — NVIDIA, AMD, or Intel GPUs via DirectML (NVIDIA can use CUDA/TensorRT),
  or **CPU-only**. Pick your device in Settings.
- **Auto-adapts** to resolution, per-monitor DPI, and refresh rate; the overlay follows the target
  window as it moves, resizes, and scrolls.
- **Many languages** — Chinese→English is the tuned default, with a multi-language architecture
  (other source/target pairs selectable in Settings).
- **Custom glossary** — pin your own translations for names, jargon, or app-specific terms.
- **Private by design** — fully offline; screen content never leaves the PC.

## How it works

```
capture → change-gate → debounce → cache → detect text → recognize → translate → layout → render
```

A decoupled pipeline keeps the overlay smooth even while inference runs:

1. **Capture** the target as GPU frames (Windows.Graphics.Capture for windows; DXGI Desktop
   Duplication for monitors; GPU crop for regions).
2. **Change-gate** — only regions that actually changed move forward (dirty-rectangles + a
   perceptual tile hash), so the models do far less work.
3. **Recognize** Chinese with on-device OCR (PP-OCRv5, or the built-in Windows OCR as a fallback).
4. **Translate** on-device; recurring lines come straight from a translation-memory cache.
5. **Render** the English in place on a transparent, click-through DirectComposition overlay,
   paced to your monitor's refresh rate.

## Requirements

- **Windows 11** (recommended) or **Windows 10 version 1903+**.
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** to build from source (`dotnet --version` ≥ 10).
- Any DX12 GPU is used automatically; CPU-only works too.
- For the zero-download OCR path: the **Chinese OCR language feature** (see setup below).

## Install & run

> Pre-built installers are planned. For now, build and run from source.

```bash
git clone https://github.com/amantu-qbit/LingoLens.git
cd LingoLens

# build everything
dotnet build LingoLens.slnx -c Release

# run the app
dotnet run --project src/LingoLens.App -c Release
```

The floating control bar appears at the bottom-center of your screen.

## First-run setup (models & language pack)

LingoLens works out of the box with the lightweight path, and downloads better models on
demand.

### 1. Chinese OCR via Windows (no download)
Install the Chinese OCR feature so the built-in recognizer works:
**Settings → Time & language → Language & region → add Chinese (Simplified) →
Language options → install "Optical character recognition".** Repeat for Traditional if needed.

### 2. Higher-accuracy models (downloaded on first use)
On first run, LingoLens downloads models into
`%LOCALAPPDATA%\LingoLens\models\`:

| Model | Used for | Size | License |
|---|---|---|---|
| PP-OCRv5 (mobile) | Best-in-class Chinese OCR | ~22 MB | Apache-2.0 |
| Opus-MT zh→en | Fast CPU/GPU translation | ~110 MB (int8) | CC-BY-4.0 |
| Qwen3-4B (GGUF) | Highest-quality translation | ~2.5 GB | Apache-2.0 |

See [`docs/MODELS.md`](docs/MODELS.md) for exact sources and checksums.

### 3. (Optional) Enable the Qwen3 quality translator
Qwen3 runs through llama.cpp and needs a native backend. Add the one matching your hardware to
`src/LingoLens.App`:

```bash
# NVIDIA
dotnet add src/LingoLens.App package LLamaSharp.Backend.Cuda12
# Any GPU (portable)
dotnet add src/LingoLens.App package LLamaSharp.Backend.Vulkan
# CPU only
dotnet add src/LingoLens.App package LLamaSharp.Backend.Cpu
```

Without a backend, LingoLens automatically falls back to Opus-MT, which needs no backend.

## Using LingoLens

1. **Launch** the app — the control bar appears (drag it anywhere).
2. Click **Translate**. The first time, choose what to translate:
   - **A window** — pick from the list (e.g. Telegram, Discord, a Chinese program). It is captured
     even if it's behind other windows.
   - **A region** — click *Draw region…* and drag a box over any part of the screen.
   - **A monitor** — pick a display and click *Use monitor*.
3. English appears over the Chinese. The overlay is **click-through** — keep using the app
   underneath normally.
4. Click **Stop** to end, or switch targets anytime.

The control bar shows live **latency** and **cache hit-rate** while running.

## Settings

Open the gear icon on the control bar:

- **Compute device** — *Auto* (fastest available) or pick a specific GPU / CPU. The selected
  **model tier** (Light / Balanced / Quality) is shown.
- **Languages** — source and target. *Chinese → English* by default; choose *Auto-detect* source
  or other targets.
- **Overlay** — display style (replace-in-place / floating panel / on-demand), backplate opacity,
  auto-contrast, minimum text scale.
- **Glossary** — add `source → translation` overrides that always win over machine translation.

## Configuration reference

Defaults live in `src/LingoLens.App/appsettings.json` under `LingoLens`:

| Section | Key | Default | Meaning |
|---|---|---|---|
| Capture | `MaxFps` | 30 | Upper bound on capture rate (adaptive cadence may go lower) |
| Capture | `ShowCaptureBorder` | false | Show the OS capture border |
| Ocr | `Engine` | `auto` | `ppocr-v5`, `windows`, or `auto` |
| Ocr | `MinConfidence` | 0.55 | Drop OCR results below this confidence |
| Translation | `SourceLanguage` / `TargetLanguage` | `zh` / `en` | Language pair |
| Translation | `Engine` | `auto` | `qwen3`, `opus-mt`, or `auto` |
| Translation | `ContextLines` | 3 | Prior lines fed to the LLM for chat coherence |
| Overlay | `Style` | `ReplaceInPlace` | Overlay presentation |
| Overlay | `BackplateOpacity` | 0.72 | Backplate translucency |
| Compute | `Device` | `auto` | `auto` or a device id |
| Compute | `MaxTier` | `auto` | Cap the model tier |
| Pipeline | `LatencyBudgetMs` | 150 | Target end-to-end latency |

## Performance & tuning

- Most real usage is **cache-bound** — repeated chat lines and menus resolve instantly.
- On a discrete GPU, expect fresh text well under the 150 ms budget; on CPU-only, prefer the
  Opus-MT translator and the Windows OCR engine.
- Lower `MaxFps` to reduce power draw; raise it for fast-moving subtitles.

## Troubleshooting

| Symptom | Fix |
|---|---|
| No Chinese is detected | Install the Windows **Chinese OCR** language feature (see setup), or let the PP-OCRv5 model finish downloading. |
| The target shows a black overlay / "protected content" | The window uses DRM/screen-capture protection (e.g. some video or banking apps). This is enforced by Windows and cannot be captured. |
| Translation quality is only okay | Enable the **Qwen3** quality translator (add a LLamaSharp backend) and select a GPU in Settings. |
| Overlay doesn't follow the window | Make sure you picked the **window** mode (not region) so it tracks moves/resizes. |
| Clicks aren't passing through | Click outside the small control bar — the overlay itself is click-through. |
| Multi-monitor / high-DPI looks off | Ensure Windows is up to date; per-monitor-v2 DPI is used, but report any monitor where placement drifts. |

## Project structure

```
src/
  LingoLens.Core         contracts, DTOs, config, cache/glossary
  LingoLens.Compute      device enumeration + execution-provider selection
  LingoLens.Capture      Windows.Graphics.Capture / DXGI / region + change gate
  LingoLens.Ocr          PP-OCRv5 (ONNX) + Windows OCR
  LingoLens.Translation  Qwen3 + Opus-MT + caching + model downloader
  LingoLens.Pipeline     orchestration: lanes, debounce, cadence, metrics
  LingoLens.Overlay      DirectComposition + Direct2D/DirectWrite rendering
  LingoLens.App          WPF control HUD, settings, target picker (host)
tests/                          unit tests
docs/                           design, running, and model notes
```

## Support

LingoLens is **free for everyone** under the MIT license. If it saves you time — especially if
you use it **commercially** — please consider chipping in. It directly funds development and is
hugely appreciated (but never required).

- ☕ **Buy Me a Coffee:** https://buymeacoffee.com/amantukhan
- ₿ **Bitcoin (BTC):** `1LQYtBNQsFq7myLAjFNrxrWPaZ7WEGLkFD`
- Ξ **Ethereum (ERC-20):** `0x8e97b63448652124e386884772efdd216b3964af`
- ₮ **USDT (Tron / TRC-20):** `TCY5Ds14UgZfaLX3seWzDRWGmS4bFMjyan`

## Privacy

LingoLens is **fully offline**. Capture, OCR, and translation all run locally. No screen
content, text, or telemetry is transmitted. The only network access is the optional one-time
download of model files from their official sources.

## Credits & licenses

LingoLens uses these open models and libraries:

- **PP-OCRv5 / RapidOCR** — OCR models (Apache-2.0).
- **Opus-MT (Helsinki-NLP / OPUS-MT)** — zh→en translation (CC-BY-4.0). *Translation model by the
  Language Technology Research Group at the University of Helsinki.*
- **Qwen3** — translation LLM (Apache-2.0).
- **ONNX Runtime**, **LLamaSharp** / llama.cpp, **Vortice.Windows** — inference & rendering.

LingoLens itself is released under the **[MIT License](LICENSE)** — free to use, modify, and
distribute, including commercially. See [`LICENSE`](LICENSE) for the full text and third-party
model terms.
