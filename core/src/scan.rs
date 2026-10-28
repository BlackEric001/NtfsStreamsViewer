//! Asynchronous directory/volume scanner with progress events and
//! cancellation support.
//!
//! The scanner walks the filesystem, queries the backend for alternate data
//! streams of every entry and emits aggregated progress updates plus a
//! `streamFound` event per discovered stream.

use std::collections::VecDeque;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::fs;
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

use crate::backend::AdsBackend;
use crate::error::CoreError;
use crate::types::{ScanOptions, ScanProgress, ScanResult};

/// Events emitted by a running scan job.
#[derive(Debug, Clone)]
pub enum ScanEvent {
    Progress(ScanProgress),
    StreamFound(crate::types::StreamInfo),
    Error { path: PathBuf, error: String },
    Completed(ScanResult),
}

/// Handle for a running scan job (currently just its id).
#[derive(Debug, Clone)]
pub struct ScanJobHandle {
    pub job_id: String,
}

/// Minimum interval between aggregated progress events (SPEC §8:
/// "Прогресс агрегировать — не отправлять событие на каждый файл").
const PROGRESS_INTERVAL: Duration = Duration::from_millis(250);

/// Run a scan over `root`, sending events into `tx`.
///
/// This is intended to be spawned on the tokio runtime. Returns the final
/// [`ScanResult`] (also delivered as [`ScanEvent::Completed`]).
pub async fn run_scan(
    backend: Arc<dyn AdsBackend>,
    job_id: String,
    root: PathBuf,
    options: ScanOptions,
    cancel: CancellationToken,
    tx: mpsc::Sender<ScanEvent>,
) -> ScanResult {
    let started = Instant::now();
    let mut progress = ScanProgress::default();
    let mut errors: Vec<String> = Vec::new();
    let mut last_emit = Instant::now() - PROGRESS_INTERVAL;

    if !root.exists() {
        let err = format!("Path not found: {}", root.display());
        errors.push(err.clone());
        let _ = tx
            .send(ScanEvent::Error {
                path: root.clone(),
                error: err,
            })
            .await;
        let result = finish(job_id, &progress, errors, started);
        let _ = tx.send(ScanEvent::Completed(result.clone())).await;
        return result;
    }

    // BFS queue. The root is enqueued with a depth of -1 so that it is always
    // expanded when recursion is enabled, regardless of the `include_directories`
    // option (which only applies to nested directories).
    let mut queue: VecDeque<(PathBuf, i32)> = VecDeque::new();
    queue.push_back((root.clone(), -1));

    while let Some((entry, depth)) = queue.pop_front() {
        if cancel.is_cancelled() {
            break;
        }

        let meta = if options.follow_symlinks {
            fs::metadata(&entry).await
        } else {
            fs::symlink_metadata(&entry).await
        };

        let meta = match meta {
            Ok(m) => m,
            Err(e) => {
                record_error(&mut progress, &mut errors, &entry, &e.to_string());
                emit_if_due(&mut last_emit, &tx, &job_id, &progress).await;
                continue;
            }
        };

        // Hidden/system filtering (unix flavour: dot-prefixed names are hidden).
        if !options.include_hidden && is_hidden_name(&entry) {
            continue;
        }

        let is_dir = meta.is_dir();
        if is_dir {
            progress.dirs_scanned += 1;
        } else {
            progress.files_scanned += 1;
        }

        if !(is_dir && !options.include_directories) {
            match backend.list_streams(&entry).await {
                Ok(streams) => {
                    for s in streams {
                        progress.streams_found += 1;
                        if tx.send(ScanEvent::StreamFound(s)).await.is_err() {
                            // Receiver gone — abort scan.
                            break;
                        }
                    }
                }
                Err(CoreError::Cancelled) => break,
                Err(e) => {
                    // Non-NTFS / unsupported entries simply have no streams.
                    if !matches!(e, CoreError::NotSupported(_)) {
                        record_error(&mut progress, &mut errors, &entry, &e.to_string());
                    }
                }
            }
        }

        progress.current_path = Some(entry.clone());
        emit_if_due(&mut last_emit, &tx, &job_id, &progress).await;

        if is_dir && (depth < 0 || options.recursive) {
            match fs::read_dir(&entry).await {
                Ok(mut rd) => loop {
                    match rd.next_entry().await {
                        Ok(Some(de)) => queue.push_back((de.path(), depth + 1)),
                        Ok(None) => break,
                        Err(e) => {
                            record_error(&mut progress, &mut errors, &entry, &e.to_string());
                            break;
                        }
                    }
                },
                Err(e) => {
                    record_error(&mut progress, &mut errors, &entry, &e.to_string());
                }
            }
        }
    }

    // Final progress snapshot, then completion.
    let _ = tx
        .send(ScanEvent::Progress(progress.clone()))
        .await;
    let result = finish(job_id, &progress, errors, started);
    let _ = tx.send(ScanEvent::Completed(result.clone())).await;
    result
}

fn finish(
    job_id: String,
    progress: &ScanProgress,
    errors: Vec<String>,
    started: Instant,
) -> ScanResult {
    ScanResult {
        job_id,
        total_files: progress.files_scanned,
        total_streams: progress.streams_found,
        errors,
        duration_ms: started.elapsed().as_millis() as u64,
    }
}

fn record_error(progress: &mut ScanProgress, errors: &mut Vec<String>, path: &Path, msg: &str) {
    progress.errors += 1;
    let full = format!("{}: {}", path.display(), msg);
    // Keep memory bounded for very large scans.
    if errors.len() < 1000 {
        errors.push(full);
    }
    let _ = &*msg;
}

fn is_hidden_name(path: &Path) -> bool {
    path.file_name()
        .map(|n| n.to_string_lossy().starts_with('.'))
        .unwrap_or(false)
}

async fn emit_if_due(
    last_emit: &mut Instant,
    tx: &mpsc::Sender<ScanEvent>,
    job_id: &str,
    progress: &ScanProgress,
) {
    if last_emit.elapsed() >= PROGRESS_INTERVAL {
        *last_emit = Instant::now();
        let mut p = progress.clone();
        p.current_path = progress.current_path.clone();
        let _ = job_id;
        let _ = tx.send(ScanEvent::Progress(p)).await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::MockBackend;
    use crate::types::ScanOptions;
    use async_trait::async_trait;
    use tempfile::TempDir;

    async fn collect_with_cancel(
        backend: Arc<dyn AdsBackend>,
        root: PathBuf,
        options: ScanOptions,
        cancel: CancellationToken,
    ) -> (Vec<ScanEvent>, ScanResult) {
        let (tx, mut rx) = mpsc::channel(1024);
        let handle = tokio::spawn(run_scan(backend, "job-1".into(), root, options, cancel, tx));
        let mut events = Vec::new();
        while let Some(ev) = rx.recv().await {
            events.push(ev);
        }
        let result = handle.await.unwrap();
        (events, result)
    }

    async fn collect(
        backend: Arc<dyn AdsBackend>,
        root: PathBuf,
        options: ScanOptions,
    ) -> (Vec<ScanEvent>, ScanResult) {
        collect_with_cancel(backend, root, options, CancellationToken::new()).await
    }

    fn streams_found(events: &[ScanEvent]) -> Vec<String> {
        let mut v: Vec<String> = events
            .iter()
            .filter_map(|e| match e {
                ScanEvent::StreamFound(s) => Some(format!(
                    "{}:{}",
                    s.owner_path.file_name().unwrap().to_string_lossy(),
                    s.name
                )),
                _ => None,
            })
            .collect();
        v.sort();
        v
    }

    #[tokio::test]
    async fn scan_finds_streams_recursively() {
        let tmp = TempDir::new().unwrap();
        let dir = tmp.path().join("sub");
        std::fs::create_dir_all(&dir).unwrap();
        let f1 = tmp.path().join("a.txt");
        let f2 = dir.join("b.dat");
        std::fs::write(&f1, b"main").unwrap();
        std::fs::write(&f2, b"main2").unwrap();

        let be = MockBackend::new();
        be.add_stream(&f1, "Zone.Identifier", b"[ZoneTransfer]");
        be.add_stream(&f2, "prompt", b"hi");

        let (events, result) = collect(be, tmp.path().to_path_buf(), ScanOptions::default()).await;
        assert_eq!(streams_found(&events), vec!["a.txt:Zone.Identifier", "b.dat:prompt"]);
        assert_eq!(result.total_streams, 2);
        assert_eq!(result.total_files, 2);
        assert!(matches!(events.last(), Some(ScanEvent::Completed(_))));
    }

    #[tokio::test]
    async fn scan_non_recursive_skips_subdirs() {
        let tmp = TempDir::new().unwrap();
        let dir = tmp.path().join("sub");
        std::fs::create_dir_all(&dir).unwrap();
        let f1 = tmp.path().join("a.txt");
        let f2 = dir.join("b.dat");
        std::fs::write(&f1, b"main").unwrap();
        std::fs::write(&f2, b"main2").unwrap();

        let be = MockBackend::new();
        be.add_stream(&f1, "s1", b"x");
        be.add_stream(&f2, "s2", b"y");

        let opts = ScanOptions {
            recursive: false,
            ..Default::default()
        };
        let (events, result) = collect(be, tmp.path().to_path_buf(), opts).await;
        assert_eq!(streams_found(&events), vec!["a.txt:s1"]);
        assert_eq!(result.total_streams, 1);
    }

    #[tokio::test]
    async fn scan_missing_root_reports_error_and_completes() {
        let be = MockBackend::new();
        let missing = PathBuf::from("/definitely-not-here-ads-test");
        let (events, result) = collect(be, missing, ScanOptions::default()).await;
        assert!(matches!(events.first(), Some(ScanEvent::Error { .. })));
        assert_eq!(result.errors.len(), 1);
        assert!(matches!(events.last(), Some(ScanEvent::Completed(_))));
    }

    #[tokio::test]
    async fn scan_records_access_errors_but_ignores_not_supported() {
        let tmp = TempDir::new().unwrap();
        let bad = tmp.path().join("locked.txt");
        let ok = tmp.path().join("ok.txt");
        std::fs::write(&bad, b"x").unwrap();
        std::fs::write(&ok, b"y").unwrap();

        let be = MockBackend::new();
        // AccessDenied on one file — must be recorded as an error.
        be.fail_on(&bad, "denied");
        // Other files return empty lists (like non-NTFS NotSupported handled upstream).

        let (_, result) = collect(be, tmp.path().to_path_buf(), ScanOptions::default()).await;
        assert_eq!(result.errors.len(), 1);
        // The per-path error is reported in the final result; the scanner does
        // not emit a separate ScanEvent::Error for backend listing failures.
        assert!(result.errors[0].contains("locked.txt"));
        assert!(result.errors[0].contains("denied"));
    }

    #[tokio::test]
    async fn scan_not_supported_error_is_ignored() {
        struct NotSupportedBackend;

        #[async_trait]
        impl AdsBackend for NotSupportedBackend {
            async fn list_volumes(&self) -> Result<Vec<crate::types::VolumeInfo>, CoreError> {
                Ok(Vec::new())
            }
            async fn list_streams(&self, _path: &Path) -> Result<Vec<crate::types::StreamInfo>, CoreError> {
                Err(CoreError::NotSupported("not an NTFS volume".into()))
            }
            async fn read_stream(
                &self,
                _path: &Path,
                _stream_name: &str,
                _offset: u64,
                _len: usize,
            ) -> Result<(Vec<u8>, bool), CoreError> {
                Err(CoreError::NotSupported("not an NTFS volume".into()))
            }
            async fn export_stream(
                &self,
                _path: &Path,
                _stream_name: &str,
                _destination: &Path,
                _cancel: CancellationToken,
            ) -> Result<u64, CoreError> {
                Err(CoreError::NotSupported("not an NTFS volume".into()))
            }
        }

        let tmp = TempDir::new().unwrap();
        std::fs::write(tmp.path().join("f.txt"), b"x").unwrap();

        let (_, result) = collect(Arc::new(NotSupportedBackend), tmp.path().to_path_buf(), ScanOptions::default()).await;
        // NotSupported means "no streams here" — it must not count as an error.
        assert!(result.errors.is_empty());
        assert_eq!(result.total_files, 1);
        assert_eq!(result.total_streams, 0);
    }

    #[tokio::test]
    async fn scan_cancel_midway_emits_completed() {
        use std::sync::atomic::{AtomicUsize, Ordering};

        // A backend that cancels the job after the first stream listing.
        struct CancellingBackend {
            cancel: CancellationToken,
            calls: AtomicUsize,
        }

        #[async_trait]
        impl AdsBackend for CancellingBackend {
            async fn list_volumes(&self) -> Result<Vec<crate::types::VolumeInfo>, CoreError> {
                Ok(Vec::new())
            }
            async fn list_streams(&self, _path: &Path) -> Result<Vec<crate::types::StreamInfo>, CoreError> {
                if self.calls.fetch_add(1, Ordering::SeqCst) == 0 {
                    self.cancel.cancel();
                }
                Ok(vec![])
            }
            async fn read_stream(
                &self,
                _path: &Path,
                _stream_name: &str,
                _offset: u64,
                _len: usize,
            ) -> Result<(Vec<u8>, bool), CoreError> {
                Err(CoreError::Cancelled)
            }
            async fn export_stream(
                &self,
                _path: &Path,
                _stream_name: &str,
                _destination: &Path,
                _cancel: CancellationToken,
            ) -> Result<u64, CoreError> {
                Err(CoreError::Cancelled)
            }
        }

        let tmp = TempDir::new().unwrap();
        for i in 0..5 {
            std::fs::write(tmp.path().join(format!("f{i}.txt")), b"x").unwrap();
        }

        let cancel = CancellationToken::new();
        let be = Arc::new(CancellingBackend {
            cancel: cancel.clone(),
            calls: AtomicUsize::new(0),
        });
        let (events, result) =
            collect_with_cancel(be, tmp.path().to_path_buf(), ScanOptions::default(), cancel).await;
        // The job still terminates with a Completed event…
        assert!(matches!(events.last(), Some(ScanEvent::Completed(_))));
        // …and the remaining files were not scanned (cancellation took effect).
        assert!(result.total_files < 5);
    }

    #[tokio::test]
    async fn scan_cancellation_stops_work() {
        let tmp = TempDir::new().unwrap();
        for i in 0..50 {
            std::fs::write(tmp.path().join(format!("f{i}.txt")), b"x").unwrap();
        }
        let be = MockBackend::new();
        let cancel = CancellationToken::new();
        cancel.cancel(); // already cancelled — loop must break immediately
        let (tx, mut rx) = mpsc::channel(8);
        let result = run_scan(
            be,
            "job-x".into(),
            tmp.path().to_path_buf(),
            ScanOptions::default(),
            cancel,
            tx,
        )
        .await;
        assert_eq!(result.total_files, 0);
        // Completed event is still emitted so clients can finalize the job.
        let mut saw_completed = false;
        while let Ok(ev) = rx.try_recv() {
            if matches!(ev, ScanEvent::Completed(_)) {
                saw_completed = true;
            }
        }
        assert!(saw_completed);
    }
}
