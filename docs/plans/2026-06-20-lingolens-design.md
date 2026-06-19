# LingoLens — Design

> Status: **Approved** (2026-06-20). On-device, transparent screen-translation overlay for Windows.
> Owner decisions locked: Win10 1903+ supported (Win11 best) · Qwen3 quality translator + Opus-MT fallback (Apache/MIT only) · Velopack installer w/ first-run model download · all three capture modes · zh→en on a multi-language architecture · both use cases · replace-in-place overlay · C#/.NET 10 single process.

## 1. Problem & goals

Many Chinese-only desktop apps, and Telegram/Discord communities that chat in Chinese, are unusable to a non-Chinese reader. **LingoLens** runs a transparent, click-through overlay that detects on-screen Chinese text and renders an English translation **in place**, nearly instantly, fully on-device (no paid cloud APIs), with **Google-Translate-class accuracy**.

Hard requirements:

- **On-device / no expensive APIs.** Screen content never leaves the machine (privacy is a feature). Optional pluggable online/self-hosted endpoint may be added later, off by default.
- **Any hardware, distributable.** One installer runs on NVIDIA / AMD / Intel GPUs and CPU-only. User can **select the compute device**.
- **Nearly instant.** ~35–85 ms compute for fresh text (target p95 < 150 ms), < 5 ms for cached/recurring text.
- **Auto-adapts** to resolution, DPI, and refresh rate; follows windows as they move/resize.
- **Elite UI/UX**, top-tier engineering.

## 2. Scope

- **OS:** Windows 11 (best path) + Windows 10 1903+ (graceful degradation via feature-probing).
- **Capture modes (all three):** chosen window, user-drawn region, full monitor.
- **Languages:** Chinese→English first, on a many→many architecture (target/source pluggable).
- **Use cases tuned equally:** live chat (Telegram/Discord) and static app UIs/menus.
- **Licensing:** only permissive models (Apache-2.0 / MIT). Excludes NLLB (CC-BY-NC); avoids Gemma custom terms.

## 3. Architecture overview

Single-process C#/.NET 10 app, modular providers wired by DI. Three decoupled lanes connected by bounded **latest-wins, drop-oldest** queues. The **UI/compositor lane is vsync-locked and never degrades** even when inference lags.

```
                ┌──────────────────────────── Capture lane (per target FPS) ───────────────────────────┐
 target (window / region / monitor) ─► Capture (WGC | DXGI | region crop, GPU texture)
                                          │
                                          ▼
                               Change-gate (WGC DirtyRegion | DXGI dirty/move rects | GPU tile-diff+pHash)
                                          │  (rejects ~65%+ frames; scroll/move ⇒ reposition only)
                                          ▼
                               Debounce/settle (chat ~40-60ms, UI ~80-120ms)
                                          │
                ┌────────────────────────┴──────── Inference lane (bounded queue) ───────────────────────┐
                ▼
        Cache lookup (region-hash→text, text→translation, glossary)  ── hit ⇒ <5ms ─────────┐
                │ miss                                                                        │
                ▼                                                                             │
        Detect (PP-OCRv5 DBNet, dirty crop) ─► Recognize (PP-OCRv5 rec / Win OCR) ─► Translate (Qwen3 | Opus-MT) │
                │                                                                             │
                ▼                                                                             ▼
        Layout (DirectWrite measure, auto-fit) ─────────────────────────────────► Overlay model (boxes+text)
                                                                                              │
                ┌─────────────────────────── UI lane (vsync-locked) ─────────────────────────┘
                ▼
        Render (DirectComposition + Direct2D, per-pixel-alpha click-through, temporal smoothing, fades)
```

## 4. Component picks

| Concern | Pick | Why |
|---|---|---|
| Stack | C#/.NET 10, single process | DirectML = one binary on all DX12 GPUs; first-class WGC + ONNX bindings; clean installer; lighter than Electron, faster to elite UI than Rust |
| Capture | **Windows.Graphics.Capture** (per-window, D3D11 GPU texture) + **DXGI Desktop Duplication** (full monitor) + GPU crop (region) | WGC sees occluded windows, cross-GPU, per-monitor DPI; DXGI for whole-display |
| Change gate | WGC `DirtyRegionMode` (24H2+) → DXGI dirty/move rects → GPU compute tile-diff + 64-bit dHash | Skip ≥65% of frames before any model |
| OCR | **RapidOCR / PP-OCRv5** (ONNX Runtime) primary; **Windows.Media.Ocr** zero-footprint fallback | Best Chinese (simp+trad+mixed) accuracy + polygon boxes; Apache-2.0 |
| Translation | **Qwen3** (LLM, GPU, quality) primary; **Opus-MT / M2M-100** (CTranslate2/ONNX int8) fallback | Near-Google zh→en on informal chat; both clean licenses; LLM fallback keeps CPU usable |
| Inference runtime | **ONNX Runtime** with EP ladder: TensorRT → CUDA → **DirectML** → OpenVINO → CPU INT8 | DirectML universal default; auto-upgrade to TensorRT/CUDA on NVIDIA |
| Overlay | Native **DirectComposition + Direct2D/DirectWrite** (per-pixel-alpha, click-through) | Top of polish/refresh-sync curve; WPF chrome alongside |
| Chrome (HUD/settings) | **WPF** custom dark theme (Mica/acrylic) | Mature, themeable, same process |
| Installer | **Velopack** (small stub, delta auto-update) + first-run model download (checksum-verified; offline bundle option) | Small installer, models matched to detected tier |

## 5. Compute-device layer ("any hardware + select device")

`IComputeDeviceManager`:

- Enumerates DXGI adapters + ONNX execution providers; ranks **TensorRT → CUDA → DirectML → OpenVINO → CPU**.
- First-run micro-benchmark picks the best automatically.
- **User override** in Settings: *Auto* / specific GPU / force CPU.
- Detects VRAM/RAM → selects a **model tier**:

| Tier | Trigger | OCR | Translator |
|---|---|---|---|
| **Quality** | ≥ 6 GB VRAM GPU | PP-OCRv5 (FP16, TensorRT/CUDA) | Qwen3-4B int4 |
| **Balanced** | any DX12 GPU | PP-OCRv5 mobile (FP16, DirectML) | Qwen3-1.7B int4 / Opus-MT int8 |
| **Light** | CPU-only / weak | Win OCR or PP-OCRv5 mobile INT8 | Opus-MT / M2M-100 int8 |

Hot-swaps providers on device-lost; tier + device overridable.

## 6. Replace-in-place overlay (elite UI)

- English drawn over the original Chinese, anchored to its polygon box, on a **frosted translucent backplate** whose tint is sampled from underlying pixels for guaranteed contrast.
- **Auto-fit:** DirectWrite measure → shrink/wrap English into the Chinese footprint; soft shadow + thin outline for legibility on any background.
- **Temporal smoothing** of boxes + strings to kill flicker; smooth fade in/out.
- Minimal, beautiful floating **control bar** (start/stop, target picker, language, device, settings); draggable, auto-hiding.

## 7. Adaptivity

- **PerMonitor-V2 DPI**; re-layout on DPI/monitor change; follow target via `SetWinEventHook` (`EVENT_OBJECT_LOCATIONCHANGE`, foreground/minimize/destroy).
- **Refresh-rate-paced** render (waitable DXGI swapchain, `SetMaximumFrameLatency=1`); correct on 60/120/144/240 Hz + hotplug.
- **Adaptive cadence:** capture FPS = min(refresh, content-change EWMA, queue/thermal cap). Idle event-driven · light 10–15 · active 30 · burst up-to-refresh. Backpressure widens debounce instead of dropping quality.

## 8. Caching & quality

- Two-level **LRU** (region-dHash→text, normalized-text→translation) namespaced per target language → recurring chat/menus instant.
- User **glossary** for term overrides (accuracy).
- Prior-line **context** fed to the LLM translator for chat coherence.
- Greedy decode (temp 0), chat-aware system prompt ("translate casually, keep emoji, don't explain").
- Low-confidence OCR is suppressed rather than shown wrong.

## 9. Reliability & edge cases

- **DRM/protected windows** return black by design → detect all-black/identical → show "protected content" badge (no bypass).
- **Fullscreen-exclusive** apps → guidance to borderless / full-monitor mode.
- **Missing model / language pack** → guided in-app download.
- **GPU lost / device removed** → fall back down the EP ladder.

## 10. Concurrency model

- **Capture thread**: WGC/DXGI event loop, GPU textures only.
- **Gate**: cheap, on capture thread or light worker.
- **Inference workers**: bounded queue, drop-oldest, latest-wins; OCR + MT.
- **UI/render thread**: vsync-locked, never blocked by inference.
- Thread-safe shared cache + glossary.

## 11. Testing

- Golden Chinese screenshot set (chat + UI) → OCR accuracy regression.
- zh→en quality eval (COMET / LLM-judge) incl. slang/emoji.
- Per-stage latency benchmarks with asserted budgets (p95 < 150 ms fresh, < 5 ms cached).
- Device matrix (NVIDIA/AMD/Intel/CPU), DPI/multi-monitor/refresh, flicker.

## 12. Distribution

- **Velopack** stub installer (~40–60 MB); models downloaded on first run matched to detected tier, checksum-verified; offline-bundle option.
- **Privacy-first:** no telemetry by default; nothing screen-related transmitted.

## 13. Solution layout

```
src/
  LingoLens.Core         (net8.0)                      abstractions, DTOs, config, DI contracts
  LingoLens.Compute      (net8.0)                      device manager, EP ladder, model tiering, benchmark
  LingoLens.Capture      (net8.0-windows10.0.19041.0)  WGC, DXGI, region, change-gate
  LingoLens.Ocr          (net8.0-windows10.0.19041.0)  PP-OCRv5 (ONNX) + Windows.Media.Ocr
  LingoLens.Translation  (net8.0)                      Qwen3 / Opus-MT providers, cache, glossary
  LingoLens.Pipeline     (net8.0)                      lanes, queues, orchestration, adaptive cadence
  LingoLens.Overlay      (net8.0-windows10.0.19041.0)  DirectComposition/Direct2D render, layout, smoothing
  LingoLens.App          (net8.0-windows10.0.19041.0)  WPF chrome, HUD, settings, host, wiring
tests/                          unit + integration + latency benches
tools/poc/                      optional Python quality PoC
docs/                           design, plan, architecture, decisions
```

## 14. Phasing

- **P0** (optional): Python PoC to validate OCR + MT quality/latency on real screenshots.
- **P1**: Core + Compute + Pipeline skeleton; window capture; ONNX/Win OCR; Opus-MT translate; DComp replace-in-place overlay; Auto device select; zh→en. **Buildable, runnable.**
- **P2**: region + full-monitor capture; model tiering + TensorRT/CUDA auto-upgrade; cache + glossary; adaptive cadence.
- **P3**: elite HUD + settings + device-picker UI; Velopack installer + model download + auto-update.
- **P4**: more languages; optional online-provider plugin; animation polish; accessibility.
