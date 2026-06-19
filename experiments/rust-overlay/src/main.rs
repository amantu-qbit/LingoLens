//! LingoLens overlay sidecar (experimental).
//!
//! Reads newline-delimited JSON `Command`s from stdin and drives an `Overlay` backend, replying
//! with JSON `Response`s on stdout. Default backend is `ConsoleOverlay` (portable); the native
//! Windows DirectComposition backend is the next step (see README.md).
//!
//! Try it:
//!   echo {"type":"show"}                                                  | cargo run
//!   echo {"type":"present","source_bounds":{"x":0,"y":0,"width":800,"height":600},"items":[{"id":"a","box":{"x":10,"y":10,"width":120,"height":24},"text":"Hello"}]} | cargo run

mod overlay;
mod protocol;

use std::io::{BufRead, Write};

use overlay::{ConsoleOverlay, Overlay};
use protocol::{Command, Response};

fn main() {
    // Backend selection: ConsoleOverlay today; WindowsOverlay when implemented (#[cfg(windows)]).
    let mut backend: Box<dyn Overlay> = Box::new(ConsoleOverlay::new());

    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut out = stdout.lock();

    emit(&mut out, &Response::Ready { backend: backend.name().to_string() });

    for line in stdin.lock().lines() {
        let line = match line {
            Ok(l) => l,
            Err(e) => {
                emit(&mut out, &Response::Error { message: format!("stdin: {e}") });
                break;
            }
        };
        let line = line.trim();
        if line.is_empty() {
            continue;
        }

        let cmd: Command = match serde_json::from_str(line) {
            Ok(c) => c,
            Err(e) => {
                emit(&mut out, &Response::Error { message: format!("parse: {e}") });
                continue;
            }
        };

        let (name, result) = dispatch(backend.as_mut(), cmd);
        let resp = match result {
            Ok(true) => Response::Ack { command: name },
            Ok(false) => {
                emit(&mut out, &Response::Ack { command: name });
                break; // shutdown
            }
            Err(e) => Response::Error { message: e.to_string() },
        };
        emit(&mut out, &resp);
    }
}

/// Returns (command name, Ok(keep_running) | Err).
fn dispatch(
    backend: &mut dyn Overlay,
    cmd: Command,
) -> (String, overlay::anyhow_lite::Result<bool>) {
    match cmd {
        Command::Show => ("show".into(), backend.show().map(|_| true)),
        Command::Hide => ("hide".into(), backend.hide().map(|_| true)),
        Command::SetTarget { bounds, dpi } => {
            ("set_target".into(), backend.set_target(bounds, dpi).map(|_| true))
        }
        Command::Present { source_bounds, items } => {
            ("present".into(), backend.present(source_bounds, &items).map(|_| true))
        }
        Command::Clear => ("clear".into(), backend.clear().map(|_| true)),
        Command::Shutdown => ("shutdown".into(), Ok(false)),
    }
}

fn emit(out: &mut impl Write, resp: &Response) {
    if let Ok(s) = serde_json::to_string(resp) {
        let _ = writeln!(out, "{s}");
        let _ = out.flush();
    }
}
