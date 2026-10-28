//! In-memory [`AdsBackend`] used by unit tests for the scanner and CLI.
//!
//! The map key is the owner path; the value is a list of `(stream_name, data)`
//! pairs in insertion order.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::backend::AdsBackend;
use crate::error::CoreError;
use crate::types::{StreamInfo, VolumeInfo};

#[derive(Default)]
pub struct MockBackend {
    pub streams: Arc<Mutex<HashMap<PathBuf, Vec<(String, Vec<u8>)>>>>,
    pub volumes: Arc<Mutex<Vec<VolumeInfo>>>,
    /// Owner paths that should produce an error (e.g. permission denied).
    pub fail_paths: Arc<Mutex<HashMap<PathBuf, String>>>,
}

impl MockBackend {
    pub fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    pub fn add_stream(&self, path: &Path, name: &str, data: &[u8]) {
        self.streams
            .lock()
            .unwrap()
            .entry(path.to_path_buf())
            .or_default()
            .push((name.to_string(), data.to_vec()));
    }

    pub fn add_volume(&self, v: VolumeInfo) {
        self.volumes.lock().unwrap().push(v);
    }

    pub fn fail_on(&self, path: &Path, message: &str) {
        self.fail_paths
            .lock()
            .unwrap()
            .insert(path.to_path_buf(), message.to_string());
    }

    fn lookup(&self, path: &Path, stream_name: &str) -> Option<Vec<u8>> {
        let guard = self.streams.lock().unwrap();
        guard
            .get(path)
            .and_then(|list| list.iter().find(|(n, _)| n == stream_name))
            .map(|(_, d)| d.clone())
    }
}

#[async_trait]
impl AdsBackend for MockBackend {
    async fn list_volumes(&self) -> Result<Vec<VolumeInfo>, CoreError> {
        Ok(self.volumes.lock().unwrap().clone())
    }

    async fn list_streams(&self, path: &Path) -> Result<Vec<StreamInfo>, CoreError> {
        if let Some(msg) = self.fail_paths.lock().unwrap().get(path) {
            return Err(CoreError::AccessDenied(msg.clone()));
        }
        let guard = self.streams.lock().unwrap();
        Ok(match guard.get(path) {
            Some(list) => list
                .iter()
                .map(|(name, data)| StreamInfo {
                    name: name.clone(),
                    size: data.len() as u64,
                    owner_path: path.to_path_buf(),
                })
                .collect(),
            None => Vec::new(),
        })
    }

    async fn read_stream(
        &self,
        path: &Path,
        stream_name: &str,
        offset: u64,
        len: usize,
    ) -> Result<(Vec<u8>, bool), CoreError> {
        let data = self.lookup(path, stream_name).ok_or_else(|| {
            CoreError::StreamNotFound(path.display().to_string(), stream_name.into())
        })?;
        let off = offset as usize;
        if off >= data.len() {
            return Ok((Vec::new(), true));
        }
        let end = off.saturating_add(len).min(data.len());
        Ok((data[off..end].to_vec(), end >= data.len()))
    }

    async fn export_stream(
        &self,
        path: &Path,
        stream_name: &str,
        destination: &Path,
        cancel: CancellationToken,
    ) -> Result<u64, CoreError> {
        if cancel.is_cancelled() {
            return Err(CoreError::Cancelled);
        }
        let data = self.lookup(path, stream_name).ok_or_else(|| {
            CoreError::StreamNotFound(path.display().to_string(), stream_name.into())
        })?;
        if let Some(dir) = destination.parent() {
            std::fs::create_dir_all(dir)?;
        }
        std::fs::write(destination, &data)?;
        Ok(data.len() as u64)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::runtime::Runtime;

    fn sample_volume() -> VolumeInfo {
        VolumeInfo {
            id: "vol-1".into(),
            name: "Sample".into(),
            mount_point: PathBuf::from("/mnt/sample"),
            file_system: "ntfs3".into(),
            is_ntfs: true,
            is_writable: true,
            total_size: Some(1024),
            free_size: Some(512),
        }
    }

    #[test]
    fn mock_backend_lists_added_volumes() {
        let rt = Runtime::new().unwrap();
        let be = MockBackend::new();
        be.add_volume(sample_volume());
        let vols = rt.block_on(be.list_volumes()).unwrap();
        assert_eq!(vols.len(), 1);
        assert_eq!(vols[0].id, "vol-1");
        assert!(vols[0].is_ntfs);
    }

    #[test]
    fn mock_backend_read_stream_slices_by_offset_and_len() {
        let rt = Runtime::new().unwrap();
        let be = MockBackend::new();
        let owner = PathBuf::from("/data/file.txt");
        be.add_stream(&owner, "$DATA", b"0123456789");

        let (chunk, eof) = rt
            .block_on(be.read_stream(&owner, "$DATA", 2, 3))
            .unwrap();
        assert_eq!(chunk, b"234");
        assert!(!eof);

        let (rest, eof) = rt
            .block_on(be.read_stream(&owner, "$DATA", 8, 100))
            .unwrap();
        assert_eq!(rest, b"89");
        assert!(eof);

        // Reading past the end returns empty data with eof=true.
        let (empty, eof) = rt
            .block_on(be.read_stream(&owner, "$DATA", 10, 5))
            .unwrap();
        assert!(empty.is_empty());
        assert!(eof);
    }

    #[test]
    fn mock_backend_read_unknown_stream_is_not_found() {
        let rt = Runtime::new().unwrap();
        let be = MockBackend::new();
        let owner = PathBuf::from("/data/file.txt");
        let err = rt
            .block_on(be.read_stream(&owner, "missing", 0, 10))
            .unwrap_err();
        assert!(matches!(err, CoreError::StreamNotFound(_, _)));
    }

    #[test]
    fn mock_backend_fail_on_returns_access_denied() {
        let rt = Runtime::new().unwrap();
        let be = MockBackend::new();
        let owner = PathBuf::from("/secret");
        be.fail_on(&owner, "permission denied");
        let err = rt.block_on(be.list_streams(&owner)).unwrap_err();
        assert!(matches!(err, CoreError::AccessDenied(_)));
    }

    #[test]
    fn mock_backend_export_writes_file_and_respects_cancel() {
        let rt = Runtime::new().unwrap();
        let be = MockBackend::new();
        let owner = PathBuf::from("/data/file.txt");
        be.add_stream(&owner, "notes", b"payload");

        let dir = tempfile::tempdir().unwrap();
        let dest = dir.path().join("out/notes.bin");
        let written = rt
            .block_on(be.export_stream(
                &owner,
                "notes",
                &dest,
                CancellationToken::new(),
            ))
            .unwrap();
        assert_eq!(written, 7);
        assert_eq!(std::fs::read(&dest).unwrap(), b"payload");

        let cancel = CancellationToken::new();
        cancel.cancel();
        let err = rt
            .block_on(be.export_stream(&owner, "notes", &dest, cancel))
            .unwrap_err();
        assert!(matches!(err, CoreError::Cancelled));
    }
}
