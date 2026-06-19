//! Overlay backend abstraction.
//!
//! `Overlay` is the seam the host drives. `ConsoleOverlay` is the portable default used to validate
//! the protocol end-to-end without any GUI. The real `WindowsOverlay` (transparent, click-through,
//! DirectComposition) is the next implementation step — see README.md.

use crate::protocol::{OverlayItem, RectI};

pub trait Overlay {
    /// Human-readable backend name, surfaced in the `Ready` response.
    fn name(&self) -> &'static str;
    fn show(&mut self) -> anyhow_lite::Result<()>;
    fn hide(&mut self) -> anyhow_lite::Result<()>;
    fn set_target(&mut self, bounds: RectI, dpi: f32) -> anyhow_lite::Result<()>;
    fn present(&mut self, source_bounds: RectI, items: &[OverlayItem]) -> anyhow_lite::Result<()>;
    fn clear(&mut self) -> anyhow_lite::Result<()>;
}

/// Portable overlay that just logs what it would draw — proves the host↔sidecar contract works
/// on any platform, with no windowing dependency.
pub struct ConsoleOverlay {
    visible: bool,
    target: RectI,
    dpi: f32,
}

impl ConsoleOverlay {
    pub fn new() -> Self {
        Self { visible: false, target: RectI::default(), dpi: 1.0 }
    }
}

impl Overlay for ConsoleOverlay {
    fn name(&self) -> &'static str {
        "console"
    }

    fn show(&mut self) -> anyhow_lite::Result<()> {
        self.visible = true;
        eprintln!("[overlay] show");
        Ok(())
    }

    fn hide(&mut self) -> anyhow_lite::Result<()> {
        self.visible = false;
        eprintln!("[overlay] hide");
        Ok(())
    }

    fn set_target(&mut self, bounds: RectI, dpi: f32) -> anyhow_lite::Result<()> {
        self.target = bounds;
        self.dpi = dpi;
        eprintln!("[overlay] target {:?} @ {}x", bounds, dpi);
        Ok(())
    }

    fn present(&mut self, source_bounds: RectI, items: &[OverlayItem]) -> anyhow_lite::Result<()> {
        eprintln!(
            "[overlay] present {} item(s) over {:?} (visible={})",
            items.len(),
            source_bounds,
            self.visible
        );
        for it in items {
            eprintln!("           - {:?}  \"{}\"", it.r#box, it.text);
        }
        Ok(())
    }

    fn clear(&mut self) -> anyhow_lite::Result<()> {
        eprintln!("[overlay] clear");
        Ok(())
    }
}

/// Minimal error helpers so the spike has zero external deps beyond serde.
pub mod anyhow_lite {
    pub type Result<T> = std::result::Result<T, Error>;

    #[derive(Debug)]
    pub struct Error(pub String);

    impl std::fmt::Display for Error {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            write!(f, "{}", self.0)
        }
    }

    impl std::error::Error for Error {}

    impl From<String> for Error {
        fn from(s: String) -> Self {
            Error(s)
        }
    }
}
