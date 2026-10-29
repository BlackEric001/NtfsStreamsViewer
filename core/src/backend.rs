//! Backend abstraction: platform-agnostic trait for ADS operations.

use crate::error::CoreError;
use crate::types::{StreamInfo, VolumeInfo};
use async_trait::async_trait;
use std::path::Path;
use tokio_util::sync::CancellationToken;

/// Abstraction over platform-specific ADS access (Windows native streams,
/// Linux xattr emulation on NTFS volumes).
#[async_trait]
pub trait AdsBackend: Send + Sync {
    /// List mounted volumes visible to the process.
    async fn list_volumes(&self) -> Result<Vec<VolumeInfo>, CoreError>;

    /// List alternate data streams of a file or directory
    /// (the default `::$DATA` stream is not included).
    async fn list_streams(&self, path: &Path) -> Result<Vec<StreamInfo>, CoreError>;

    /// Read up to `len` bytes of a stream starting at `offset`.
    /// Returns `(data, eof)`.
    async fn read_stream(
        &self,
        path: &Path,
        stream_name: &str,
        offset: u64,
        len: usize,
    ) -> Result<(Vec<u8>, bool), CoreError>;

    /// Export a stream to a destination file, copying in blocks.
    /// Returns number of bytes written. Must honour `cancel`.
    async fn export_stream(
        &self,
        path: &Path,
        stream_name: &str,
        destination: &Path,
        cancel: CancellationToken,
    ) -> Result<u64, CoreError>;
}

/// Concrete backend type for the current platform.
#[cfg(windows)]
pub type PlatformBackend = crate::backends::windows::WindowsBackend;
#[cfg(not(windows))]
pub type PlatformBackend = crate::backends::linux::LinuxBackend;

/// Construct the backend for the current platform.
pub fn default_backend() -> std::sync::Arc<PlatformBackend> {
    PlatformBackend::new()
}
