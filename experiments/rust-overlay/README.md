# lingolens-overlay — Rust sidecar (spike)

> **Status: experimental spike, on a branch.** This is a *starting point*, not a finished feature.
> The protocol + portable `ConsoleOverlay` backend are implemented; the native Windows overlay is
> documented below as the next step. **Not yet compiled in CI** (authored without a local Rust
> toolchain) — run `cargo build` and expect to nudge it.

## Why a Rust sidecar?

The LingoLens C# app is excellent for the inference path (DirectML/ONNX Runtime, LLamaSharp) and the
elite WPF UI. The one place Rust genuinely helps is the **always-on overlay**: a process that lives
for the whole session, holding a transparent window + GPU compositor. There, Rust's **no-GC, low
idle-RAM, small self-contained binary** profile is a real win.

This spike carves out exactly that slice: **the C# pipeline stays the brain** (capture → change-gate
→ OCR → translate), and a small Rust process becomes the **always-on overlay renderer**, driven over
a trivial JSON-line protocol. It does *not* change translation latency (that's native C++ either
way — see the repo README) — it lowers the resident footprint of the part that never sleeps.

```
 ┌───────────────────────────┐        stdin: JSON Command per line        ┌────────────────────────┐
 │  C# LingoLens pipeline      │  ───────────────────────────────────────► │ lingolens-overlay (Rust)│
 │  capture·gate·OCR·translate │  ◄─────────────────────────────────────── │ transparent click-thru  │
 └───────────────────────────┘        stdout: JSON Response per line       │ DirectComposition window │
                                                                            └────────────────────────┘
```

## Protocol (`src/protocol.rs`)

Host → sidecar (one JSON object per line on stdin):

| `type` | fields | meaning |
|---|---|---|
| `show` / `hide` | — | show/hide the overlay window |
| `set_target` | `bounds {x,y,width,height}`, `dpi` | place the overlay over a screen rect |
| `present` | `source_bounds`, `items[] {id, box, text, opacity?, background_argb?}` | replace drawn items |
| `clear` | — | remove all items |
| `shutdown` | — | exit |

Sidecar → host: `ready {backend}`, `ack {command}`, `error {message}`.

## Build & run

```bash
cd experiments/rust-overlay
cargo run            # prints `ready`, then waits for commands on stdin
# in another shell, or piped:
echo '{"type":"show"}' | cargo run
echo '{"type":"present","source_bounds":{"x":0,"y":0,"width":800,"height":600},"items":[{"id":"a","box":{"x":10,"y":10,"width":160,"height":28},"text":"Hello, world"}]}' | cargo run
```

With the default `ConsoleOverlay`, commands are logged to stderr — proving the contract before any
windowing code exists.

## Implementation plan — `WindowsOverlay`

The next step replaces `ConsoleOverlay` with a real `#[cfg(windows)]` backend using the `windows`
crate (features listed, commented, in `Cargo.toml`):

1. **Window:** `CreateWindowExW` with `WS_POPUP` and
   `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`
   on a dedicated thread with its own message pump. Per-monitor-v2 DPI via
   `SetProcessDpiAwarenessContext`.
2. **Compositor:** `D3D11CreateDevice` (BGRA) → DXGI device → `DCompositionCreateDevice` →
   `IDCompositionTarget` for the HWND → root `IDCompositionVisual` → composition swapchain
   (`CreateSwapChainForComposition`, premultiplied alpha).
3. **Draw:** `ID2D1DeviceContext` over the swapchain back-buffer; rounded translucent backplate per
   item (tint from `background_argb` when auto-contrast), text via DirectWrite with a luminance-based
   halo; `Present` vsync-paced.
4. **Map** `source_bounds → set_target.bounds` for each item box; apply `opacity`.
5. Optional later: move capture itself into the sidecar (WGC via `windows`), so the always-on path is
   fully Rust and C# is invoked only for OCR/MT.

This mirrors the proven C# implementation in `src/LingoLens.Overlay`, so it's a port, not a redesign.

## Honest caveats

- This crate currently depends only on `serde`/`serde_json` (portable) and has **not been built in
  this environment** — verify with `cargo build` and fix any nits before relying on it.
- The Windows overlay is **not written yet** — only specified. It's the bulk of the remaining work.
