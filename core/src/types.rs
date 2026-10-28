//! Domain types shared across core, CLI and (via JSON-RPC) GUI.

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// Maximum length of a single `stream.read` request (1 MiB), per SPEC §5.3.
pub const MAX_READ_LEN: usize = 1024 * 1024;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VolumeInfo {
    pub id: String,
    pub name: String,
    pub mount_point: PathBuf,
    pub file_system: String,
    pub is_ntfs: bool,
    pub is_writable: bool,
    pub total_size: Option<u64>,
    pub free_size: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StreamInfo {
    /// Stream name without the `:$DATA` suffix (`::$DATA` itself is not listed).
    pub name: String,
    pub size: u64,
    pub owner_path: PathBuf,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct ScanOptions {
    pub recursive: bool,
    pub include_hidden: bool,
    pub include_system: bool,
    pub include_directories: bool,
    pub follow_symlinks: bool,
}

impl Default for ScanOptions {
    fn default() -> Self {
        Self {
            recursive: true,
            include_hidden: true,
            include_system: false,
            include_directories: true,
            follow_symlinks: false,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ScanProgress {
    pub files_scanned: u64,
    pub dirs_scanned: u64,
    pub streams_found: u64,
    pub errors: u64,
    pub current_path: Option<PathBuf>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ScanResult {
    pub job_id: String,
    pub total_files: u64,
    pub total_streams: u64,
    pub errors: Vec<String>,
    pub duration_ms: u64,
}

/// Result of `stream.read` — chunk of stream data plus EOF flag.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReadResult {
    pub data_base64: String,
    pub offset: u64,
    pub length: u64,
    pub eof: bool,
}

/// Result of `stream.export`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExportResult {
    pub bytes_written: u64,
    pub sha256: String,
}

/// Result of `stream.exportAll`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExportAllResult {
    pub streams_exported: usize,
    pub files: Vec<PathBuf>,
}
