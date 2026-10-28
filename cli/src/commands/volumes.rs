//! `ads-cli volumes`

use anyhow::Result;
use ntfs_streams_core::{default_backend, AdsBackend};

pub async fn run() -> Result<()> {
    let backend = default_backend();
    let volumes = backend.list_volumes().await?;
    if volumes.is_empty() {
        println!("No mounted volumes found.");
        return Ok(());
    }
    println!(
        "{:<6} {:<24} {:<10} {:<6} {:>12} {:>12}  {}",
        "ID", "MOUNT", "FS", "NTFS", "TOTAL", "FREE", "WRITABLE"
    );
    println!("{}", "-".repeat(88));
    for v in &volumes {
        println!(
            "{:<6} {:<24} {:<10} {:<6} {:>12} {:>12}  {}",
            v.id,
            truncate(&v.mount_point.to_string_lossy(), 24),
            truncate(&v.file_system, 10),
            if v.is_ntfs { "yes" } else { "no" },
            v.total_size.map(crate::commands::human_size).unwrap_or_default(),
            v.free_size.map(crate::commands::human_size).unwrap_or_default(),
            if v.is_writable { "yes" } else { "no (ro)" },
        );
    }
    Ok(())
}

fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        s.to_string()
    } else {
        let cut: String = s.chars().take(max - 1).collect();
        format!("{cut}…")
    }
}
