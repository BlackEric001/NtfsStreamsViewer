//! `ads-cli export <path> [stream] -o <output> [--all]`

use std::path::{Path, PathBuf};

use anyhow::{bail, Result};
use ntfs_streams_core::{default_backend, AdsBackend};
use sha2::{Digest, Sha256};
use tokio_util::sync::CancellationToken;

pub async fn run(path: &str, stream_name: Option<&str>, output: &str, all: bool) -> Result<()> {
    let backend = default_backend();
    let owner = Path::new(path);

    if all {
        let dest_dir = PathBuf::from(output);
        std::fs::create_dir_all(&dest_dir)?;
        let streams = backend.list_streams(owner).await?;
        if streams.is_empty() {
            println!("No streams to export for {path}.");
            return Ok(());
        }
        for s in &streams {
            let out = dest_dir.join(crate::commands::export_file_name(owner, &s.name));
            let written = backend
                .export_stream(owner, &s.name, &out, CancellationToken::new())
                .await?;
            println!(
                "Exported {}:{} -> {} ({} bytes)",
                path,
                s.name,
                out.display(),
                written
            );
        }
        return Ok(());
    }

    let name = stream_name.unwrap_or_else(|| {
        // Default convenience: if there's exactly one stream, use it.
        ""
    });
    if name.is_empty() {
        bail!("stream name is required (or use --all)");
    }
    let dest = PathBuf::from(output);
    let written = backend
        .export_stream(owner, name, &dest, CancellationToken::new())
        .await?;
    let hash = sha256_file(&dest)?;
    println!(
        "Exported {path}:{name} -> {} ({written} bytes, sha256={hash})",
        dest.display()
    );
    Ok(())
}

fn sha256_file(path: &Path) -> Result<String> {
    let data = std::fs::read(path)?;
    let mut hasher = Sha256::new();
    hasher.update(&data);
    Ok(hex(&hasher.finalize()))
}

pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
