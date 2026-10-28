//! `ads-cli streams <path>`

use std::path::Path;

use anyhow::Result;
use ntfs_streams_core::{default_backend, AdsBackend};

pub async fn run(path: &str) -> Result<()> {
    let backend = default_backend();
    match backend.list_streams(Path::new(path)).await {
        Ok(streams) => {
            if streams.is_empty() {
                println!("No alternate data streams for {path}.");
            } else {
                println!("{:<30} {:>12}", "STREAM", "SIZE");
                println!("{}", "-".repeat(44));
                for s in &streams {
                    println!("{:<30} {:>12}", s.name, crate::commands::human_size(s.size));
                }
            }
            Ok(())
        }
        Err(e) => {
            eprintln!("Error: {e}");
            std::process::exit(1);
        }
    }
}
