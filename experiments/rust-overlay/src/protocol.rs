//! Wire protocol between the C# LingoLens pipeline (host) and this Rust overlay sidecar.
//!
//! The host writes one JSON object per line to the sidecar's stdin; the sidecar replies with one
//! JSON object per line on stdout. This keeps the boundary language-agnostic and trivial to debug
//! (you can drive the sidecar by hand with `echo '{...}' | lingolens-overlay`).

use serde::{Deserialize, Serialize};

/// Integer rectangle in virtual-desktop pixels (matches `RectI` in the C# Core).
#[derive(Debug, Clone, Copy, Serialize, Deserialize, Default)]
pub struct RectI {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

/// One translated item positioned over its source box (frame/source pixel space).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OverlayItem {
    /// Stable id used for temporal smoothing across frames.
    pub id: String,
    /// Where the original text sits, in `source_bounds` space.
    pub r#box: RectI,
    /// The translated text to draw.
    pub text: String,
    #[serde(default = "default_opacity")]
    pub opacity: f32,
    /// Optional ARGB background sampled by the host for auto-contrast.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub background_argb: Option<u32>,
}

fn default_opacity() -> f32 {
    1.0
}

/// Commands the host sends to the sidecar. `tag` selects the variant.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Command {
    /// Show the overlay window.
    Show,
    /// Hide the overlay window (keeps the process alive).
    Hide,
    /// Position/size the overlay over a target rectangle (virtual-desktop px) at a DPI scale.
    SetTarget { bounds: RectI, dpi: f32 },
    /// Replace the drawn items. `source_bounds` is the coordinate space `items` map into.
    Present {
        source_bounds: RectI,
        items: Vec<OverlayItem>,
    },
    /// Clear all drawn items.
    Clear,
    /// Tear down and exit.
    Shutdown,
}

/// Responses the sidecar sends back to the host.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Response {
    /// Sidecar is up and the overlay backend is ready.
    Ready { backend: String },
    /// A command was applied.
    Ack { command: String },
    /// A command failed.
    Error { message: String },
}
