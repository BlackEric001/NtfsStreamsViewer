//! `ads-cli scan <path>` — runs a scan job and prints progress/results.

use std::path::PathBuf;

use anyhow::Result;
use ntfs_streams_core::scan::{run_scan, ScanEvent};
use ntfs_streams_core::{default_backend, ScanOptions};
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

pub async fn run(path: &str, opts: ScanOptions) -> Result<()> {
    let backend = default_backend();
    let job_id = format!("scan-{}", uuid::Uuid::new_v4());
    let cancel = CancellationToken::new();
    let (tx, mut rx) = mpsc::channel::<ScanEvent>(256);

    // Ctrl-C cancels the scan.
    let cancel_clone = cancel.clone();
    tokio::spawn(async move {
        loop {
            if tokio::signal::ctrl_c().await.is_ok() {
                eprintln!("\nCancellation requested…");
                cancel_clone.cancel();
            } else {
                break;
            }
        }
    });

    let root = PathBuf::from(path);
    let handle = tokio::spawn(run_scan(backend, job_id.clone(), root, opts, cancel.clone(), tx));

    while let Some(ev) = rx.recv().await {
        match ev {
            ScanEvent::Progress(p) => {
                print!(
                    "\rScanned: {} files, {} dirs | streams: {} | errors: {}",
                    p.files_scanned, p.dirs_scanned, p.streams_found, p.errors
                );
                use std::io::Write;
                let _ = std::io::stdout().flush();
            }
            ScanEvent::StreamFound(s) => {
                println!(
                    "\n  ADS: {}:{} ({} bytes)",
                    s.owner_path.display(),
                    s.name,
                    s.size
                );
                print!("…continuing");
                use std::io::Write;
                let _ = std::io::stdout().flush();
            }
            ScanEvent::Error { path, error } => {
                eprintln!("\n  error at {}: {error}", path.display());
            }
            ScanEvent::Completed(r) => {
                println!();
                println!(
                    "Done in {} ms: {} files scanned, {} streams found, {} errors.",
                    r.duration_ms, r.total_files, r.total_streams, r.errors.len()
                );
                if !r.errors.is_empty() {
                    println!("First errors:");
                    for e in r.errors.iter().take(10) {
                        println!("  - {e}");
                    }
                }
            }
        }
    }

    let _ = handle.await;
    Ok(())
}
