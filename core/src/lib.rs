//! Core library for working with NTFS Alternate Data Streams (ADS)
//! on mounted volumes.
//!
//! This crate knows nothing about consoles, JSON or GUIs — it exposes a
//! platform-agnostic [`AdsBackend`] trait with Windows and Linux
//! implementations.

pub mod backend;
pub mod error;
pub mod scan;
pub mod types;

mod backends;
#[cfg(test)]
pub(crate) mod test_support;

pub use backend::{default_backend, AdsBackend};
pub use error::CoreError;
pub use scan::{run_scan, ScanJobHandle};
pub use types::{ReadResult, ScanOptions, ScanProgress, ScanResult, StreamInfo, VolumeInfo};

/// Library version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
