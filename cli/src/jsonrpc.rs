//! JSON-RPC 2.0 server over stdin/stdout (SPEC §5).
//!
//! Transport: one JSON message per line. Requests are answered on stdout;
//! notifications (events) are pushed asynchronously. Logs go to stderr only.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use base64::Engine as _;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::sync::{mpsc, Mutex};
use tokio_util::sync::CancellationToken;

use ntfs_streams_core::backend::AdsBackend;
use ntfs_streams_core::scan::{run_scan, ScanEvent};
use ntfs_streams_core::{CoreError, ScanOptions};

/// Outgoing line queue (responses + events), serialized by a writer task.
type Outbox = mpsc::Sender<String>;

pub struct ServerState {
    pub backend: Arc<dyn AdsBackend>,
    pub outbox: Outbox,
    /// job_id -> cancellation token for running scans
    pub jobs: Arc<Mutex<HashMap<String, CancellationToken>>>,
}

/// Serve JSON-RPC over the process stdio until EOF on stdin.
pub async fn serve_stdio() -> anyhow::Result<()> {
    let (out_tx, mut out_rx) = mpsc::channel::<String>(1024);
    let state = Arc::new(ServerState {
        backend: ntfs_streams_core::default_backend(),
        outbox: out_tx,
        jobs: Arc::new(Mutex::new(HashMap::new())),
    });

    // Writer task: owns stdout, one line per message.
    let writer = tokio::spawn(async move {
        let mut stdout = tokio::io::stdout();
        while let Some(line) = out_rx.recv().await {
            if stdout.write_all(line.as_bytes()).await.is_err() {
                break;
            }
            if stdout.write_all(b"\n").await.is_err() {
                break;
            }
            let _ = stdout.flush().await;
        }
    });

    let stdin = tokio::io::stdin();
    let mut lines = BufReader::new(stdin).lines();

    tracing::info!("ads-cli JSON-RPC server started (stdio)");

    while let Ok(Some(line)) = lines.next_line().await {
        let line = line.trim().to_string();
        if line.is_empty() {
            continue;
        }
        let state = state.clone();
        // Handle each request concurrently but responses keep their ids.
        tokio::spawn(async move {
            let response = handle_request(&state, &line).await;
            if let Some(resp) = response {
                match serde_json::to_string(&resp) {
                    Ok(s) => {
                        let _ = state.outbox.send(s).await;
                    }
                    Err(e) => tracing::error!("failed to serialize response: {e}"),
                }
            }
        });
    }

    // stdin closed — cancel all jobs and shut down.
    let jobs = state.jobs.lock().await;
    for (_, token) in jobs.iter() {
        token.cancel();
    }
    drop(jobs);
    // Drop the state's sender so the writer task ends once queued lines are flushed.
    let _ = state;
    let _ = writer.await;
    tracing::info!("ads-cli JSON-RPC server stopped");
    Ok(())
}

fn error_response(id: Value, code: i32, message: &str, data: Option<Value>) -> Value {
    let mut err = json!({"code": code, "message": message});
    if let Some(d) = data {
        err["data"] = d;
    }
    json!({"jsonrpc": "2.0", "id": id, "error": err})
}

fn core_error_response(id: Value, e: &CoreError) -> Value {
    error_response(id, e.jsonrpc_code(), &e.to_string(), None)
}

/// Parse and dispatch one line. Returns `None` for notifications.
pub(crate) async fn handle_request(state: &Arc<ServerState>, line: &str) -> Option<Value> {
    let parsed: Value = match serde_json::from_str(line) {
        Ok(v) => v,
        Err(_) => return Some(error_response(Value::Null, -32700, "Parse error", None)),
    };

    if !parsed.get("jsonrpc").and_then(|v| v.as_str()).eq(&Some("2.0")) {
        return Some(error_response(
            parsed.get("id").cloned().unwrap_or(Value::Null),
            -32600,
            "Invalid request: missing jsonrpc 2.0",
            None,
        ));
    }

    let id = parsed.get("id").cloned();
    let method = parsed.get("method").and_then(|m| m.as_str()).unwrap_or("");
    let params = parsed.get("params").cloned().unwrap_or(json!({}));

    // A message without an id is a notification from the client — ignore.
    let Some(id) = id else {
        return None;
    };

    let result = dispatch(state, method, params).await;
    match result {
        Ok(value) => Some(json!({"jsonrpc": "2.0", "id": id, "result": value})),
        Err(RpcError::MethodNotFound(m)) => Some(error_response(
            id,
            -32601,
            &format!("Method not found: {m}"),
            None,
        )),
        Err(RpcError::InvalidParams(msg)) => Some(error_response(id, -32602, &msg, None)),
        Err(RpcError::Internal(msg)) => Some(error_response(id, -32603, &msg, None)),
        Err(RpcError::Core(e)) => Some(core_error_response(id, &e)),
    }
}

enum RpcError {
    MethodNotFound(String),
    InvalidParams(String),
    Internal(String),
    Core(CoreError),
}

impl From<CoreError> for RpcError {
    fn from(e: CoreError) -> Self {
        RpcError::Core(e)
    }
}

async fn dispatch(state: &Arc<ServerState>, method: &str, params: Value) -> Result<Value, RpcError> {
    match method {
        "volumes.list" => volumes_list(state).await,
        "scan.start" => scan_start(state, params).await,
        "scan.cancel" => scan_cancel(state, params).await,
        "streams.list" => streams_list(state, params).await,
        "stream.read" => stream_read(state, params).await,
        "stream.export" => stream_export(state, params).await,
        "stream.exportAll" => stream_export_all(state, params).await,
        other => Err(RpcError::MethodNotFound(other.to_string())),
    }
}

fn param_str<'a>(params: &'a Value, key: &str) -> Result<&'a str, RpcError> {
    params
        .get(key)
        .and_then(|v| v.as_str())
        .ok_or_else(|| RpcError::InvalidParams(format!("missing string param '{key}'")))
}

// ---------- methods ----------

async fn volumes_list(state: &Arc<ServerState>) -> Result<Value, RpcError> {
    let volumes = state.backend.list_volumes().await?;
    Ok(json!({ "volumes": volumes }))
}

async fn scan_start(state: &Arc<ServerState>, params: Value) -> Result<Value, RpcError> {
    let path = param_str(&params, "path")?.to_string();
    let options = serde_json::from_value::<ScanOptions>(params.clone())
        .map_err(|e| RpcError::InvalidParams(format!("bad scan options: {e}")))?;

    let job_id = format!("scan-{}", uuid::Uuid::new_v4());
    let cancel = CancellationToken::new();
    state.jobs.lock().await.insert(job_id.clone(), cancel.clone());

    let backend = state.backend.clone();
    let outbox = state.outbox.clone();
    let jobs = state.jobs.clone();

    let event_job = job_id.clone();
    tokio::spawn(async move {
        let (tx, mut rx) = mpsc::channel::<ScanEvent>(512);
        let root = PathBuf::from(&path);
        let runner = tokio::spawn(run_scan(backend, event_job.clone(), root, options, cancel, tx));

        while let Some(ev) = rx.recv().await {
            let msg = match ev {
                ScanEvent::Progress(p) => json!({
                    "jsonrpc": "2.0",
                    "method": "scan.progress",
                    "params": {
                        "job_id": event_job,
                        "files_scanned": p.files_scanned,
                        "dirs_scanned": p.dirs_scanned,
                        "streams_found": p.streams_found,
                        "errors": p.errors,
                        "current_path": p.current_path,
                    }
                }),
                ScanEvent::StreamFound(s) => json!({
                    "jsonrpc": "2.0",
                    "method": "scan.streamFound",
                    "params": { "job_id": event_job, "stream": s }
                }),
                ScanEvent::Error { path, error } => json!({
                    "jsonrpc": "2.0",
                    "method": "scan.error",
                    "params": { "job_id": event_job, "path": path, "error": error }
                }),
                ScanEvent::Completed(r) => json!({
                    "jsonrpc": "2.0",
                    "method": "scan.completed",
                    "params": r
                }),
            };
            match serde_json::to_string(&msg) {
                Ok(s) => {
                    if outbox.send(s).await.is_err() {
                        break; // pipe closed
                    }
                }
                Err(e) => tracing::error!("event serialization failed: {e}"),
            }
        }
        let _ = runner.await;
        jobs.lock().await.remove(&event_job);
    });

    Ok(json!({ "job_id": job_id }))
}

async fn scan_cancel(state: &Arc<ServerState>, params: Value) -> Result<Value, RpcError> {
    let job_id = param_str(&params, "job_id")?;
    let mut jobs = state.jobs.lock().await;
    match jobs.remove(job_id) {
        Some(token) => {
            token.cancel();
            Ok(json!({ "cancelled": true }))
        }
        None => Ok(json!({ "cancelled": false })),
    }
}

async fn streams_list(state: &Arc<ServerState>, params: Value) -> Result<Value, RpcError> {
    let path = param_str(&params, "path")?;
    let streams = state.backend.list_streams(Path::new(path)).await?;
    Ok(json!({ "streams": streams }))
}

async fn stream_read(state: &Arc<ServerState>, params: Value) -> Result<Value, RpcError> {
    let path = param_str(&params, "path")?;
    let stream_name = param_str(&params, "stream_name")?;
    let offset = params.get("offset").and_then(Value::as_u64).unwrap_or(0);
    let length = params
        .get("length")
        .and_then(Value::as_u64)
        .unwrap_or(64 * 1024) as usize;
    if length > ntfs_streams_core::types::MAX_READ_LEN {
        return Err(RpcError::InvalidParams(
            "length must not exceed 1 MiB".into(),
        ));
    }
    let (data, eof) = state
        .backend
        .read_stream(Path::new(path), stream_name, offset, length)
        .await?;
    let b64 = base64::engine::general_purpose::STANDARD.encode(&data);
    Ok(json!({
        "data_base64": b64,
        "offset": offset,
        "length": data.len() as u64,
        "eof": eof,
    }))
}

async fn stream_export(state: &Arc<ServerState>, params: Value) -> Result<Value, RpcError> {
    let path = param_str(&params, "path")?;
    let stream_name = param_str(&params, "stream_name")?;
    let destination = param_str(&params, "destination")?;
    let written = state
        .backend
        .export_stream(
            Path::new(path),
            stream_name,
            Path::new(destination),
            CancellationToken::new(),
        )
        .await?;
    let sha = sha256_of_file(Path::new(destination))
        .map_err(|e| RpcError::Internal(format!("hashing exported file: {e}")))?;
    Ok(json!({ "bytes_written": written, "sha256": sha }))
}

async fn stream_export_all(state: &Arc<ServerState>, params: Value) -> Result<Value, RpcError> {
    let path = param_str(&params, "path")?;
    let dest_dir = param_str(&params, "destination_dir")?;
    let owner = Path::new(path);
    let dir = PathBuf::from(dest_dir);
    std::fs::create_dir_all(&dir)
        .map_err(|e| RpcError::Core(CoreError::Io(e)))?;
    let streams = state.backend.list_streams(owner).await?;
    let mut files = Vec::new();
    for s in &streams {
        let fname = export_file_name(owner, &s.name);
        let out = dir.join(&fname);
        state
            .backend
            .export_stream(owner, &s.name, &out, CancellationToken::new())
            .await?;
        files.push(out);
    }
    Ok(json!({ "streams_exported": files.len(), "files": files }))
}

// ---------- helpers ----------

fn export_file_name(owner: &Path, stream_name: &str) -> String {
    let sanitize = |s: &str| -> String {
        s.chars()
            .map(|c| match c {
                '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|' => '_',
                c if (c as u32) < 0x20 => '_',
                c => c,
            })
            .collect()
    };
    let base = owner
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| "owner".into());
    format!("{}_{}.bin", sanitize(&base), sanitize(stream_name))
}

fn sha256_of_file(path: &Path) -> std::io::Result<String> {
    let data = std::fs::read(path)?;
    let mut hasher = Sha256::new();
    hasher.update(&data);
    Ok(hasher
        .finalize()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use ntfs_streams_core::{StreamInfo, VolumeInfo};
    use tokio_util::sync::CancellationToken;
    use std::time::Duration;

    /// Backend that records nothing and returns canned data for RPC tests.
    struct FakeBackend {
        payload: Vec<u8>,
    }

    impl FakeBackend {
        fn new() -> Self {
            Self {
                payload: b"hello stream".to_vec(),
            }
        }
    }

    #[async_trait]
    impl AdsBackend for FakeBackend {
        async fn list_volumes(&self) -> Result<Vec<VolumeInfo>, CoreError> {
            Ok(vec![VolumeInfo {
                id: "vol-1".into(),
                name: "Fake".into(),
                mount_point: std::path::PathBuf::from("/mnt/fake"),
                file_system: "ntfs3".into(),
                is_ntfs: true,
                is_writable: true,
                total_size: Some(100),
                free_size: Some(50),
            }])
        }

        async fn list_streams(&self, _path: &Path) -> Result<Vec<StreamInfo>, CoreError> {
            Ok(vec![])
        }

        async fn read_stream(
            &self,
            _path: &Path,
            _stream_name: &str,
            offset: u64,
            len: usize,
        ) -> Result<(Vec<u8>, bool), CoreError> {
            let off = offset as usize;
            if off >= self.payload.len() {
                return Ok((Vec::new(), true));
            }
            let end = (off + len).min(self.payload.len());
            Ok((self.payload[off..end].to_vec(), end >= self.payload.len()))
        }

        async fn export_stream(
            &self,
            _path: &Path,
            _stream_name: &str,
            destination: &Path,
            _cancel: CancellationToken,
        ) -> Result<u64, CoreError> {
            std::fs::write(destination, &self.payload)
                .map_err(CoreError::Io)?;
            Ok(self.payload.len() as u64)
        }
    }

    fn test_state() -> Arc<ServerState> {
        let (tx, _rx) = mpsc::channel(256);
        Arc::new(ServerState {
            backend: Arc::new(FakeBackend::new()),
            outbox: tx,
            jobs: Arc::new(Mutex::new(HashMap::new())),
        })
    }

    fn req(id: Value, method: &str, params: Value) -> String {
        json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params}).to_string()
    }

    #[tokio::test]
    async fn parse_error_on_invalid_json() {
        let state = test_state();
        let resp = handle_request(&state, "not json at all").await.unwrap();
        assert_eq!(resp["error"]["code"], -32700);
        assert_eq!(resp["id"], Value::Null);
    }

    #[tokio::test]
    async fn invalid_request_without_jsonrpc_version() {
        let state = test_state();
        let line = json!({"id": 1, "method": "volumes.list"}).to_string();
        let resp = handle_request(&state, &line).await.unwrap();
        assert_eq!(resp["error"]["code"], -32600);
    }

    #[tokio::test]
    async fn notification_without_id_is_ignored() {
        let state = test_state();
        let line = json!({"jsonrpc": "2.0", "method": "volumes.list"}).to_string();
        assert!(handle_request(&state, &line).await.is_none());
    }

    #[tokio::test]
    async fn method_not_found() {
        let state = test_state();
        let resp = handle_request(&state, &req(json!(7), "no.such.method", json!({})))
            .await
            .unwrap();
        assert_eq!(resp["id"], json!(7));
        assert_eq!(resp["error"]["code"], -32601);
    }

    #[tokio::test]
    async fn volumes_list_returns_volumes() {
        let state = test_state();
        let resp = handle_request(&state, &req(json!("a"), "volumes.list", json!({})))
            .await
            .unwrap();
        let vols = resp["result"]["volumes"].as_array().unwrap();
        assert_eq!(vols.len(), 1);
        assert_eq!(vols[0]["mount_point"], "/mnt/fake");
        assert_eq!(vols[0]["is_ntfs"], true);
    }

    #[tokio::test]
    async fn missing_params_gives_invalid_params_error() {
        let state = test_state();
        let resp = handle_request(&state, &req(json!(1), "streams.list", json!({})))
            .await
            .unwrap();
        assert_eq!(resp["error"]["code"], -32602);
    }

    #[tokio::test]
    async fn stream_read_returns_base64_and_eof() {
        let state = test_state();
        let resp = handle_request(
            &state,
            &req(
                json!(2),
                "stream.read",
                json!({"path": "/x", "stream_name": "s", "offset": 0, "length": 5}),
            ),
        )
        .await
        .unwrap();
        let result = &resp["result"];
        assert_eq!(result["length"], 5);
        assert_eq!(result["eof"], false);
        let decoded = base64::engine::general_purpose::STANDARD
            .decode(result["data_base64"].as_str().unwrap())
            .unwrap();
        assert_eq!(decoded, b"hello");

        // Oversized reads are rejected (SPEC §5: max 1 MiB per read).
        let resp = handle_request(
            &state,
            &req(
                json!(3),
                "stream.read",
                json!({"path": "/x", "stream_name": "s", "length": 2 * 1024 * 1024}),
            ),
        )
        .await
        .unwrap();
        assert_eq!(resp["error"]["code"], -32602);
    }

    #[tokio::test]
    async fn stream_export_writes_file_and_hashes() {
        let tmp = tempfile::TempDir::new().unwrap();
        let dest = tmp.path().join("out.bin");
        let state = test_state();
        let resp = handle_request(
            &state,
            &req(
                json!(4),
                "stream.export",
                json!({
                    "path": "/x",
                    "stream_name": "s",
                    "destination": dest.to_string_lossy(),
                }),
            ),
        )
        .await
        .unwrap();
        assert_eq!(resp["result"]["bytes_written"], 12);
        // sha256("hello stream") must match the exported file's hash.
        let sha = resp["result"]["sha256"].as_str().unwrap();
        assert_eq!(sha.len(), 64);
        assert_eq!(
            sha,
            "21711375b9af870d28f0aec255949cb094893ac157f191055ffd3492ae657933"
        );
        assert_eq!(std::fs::read(&dest).unwrap(), b"hello stream");
    }

    #[test]
    fn test_export_file_name_sanitizes() {
        assert_eq!(
            export_file_name(Path::new("/tmp/f.txt"), "Zone.Identifier"),
            "f.txt_Zone.Identifier.bin"
        );
        assert_eq!(
            export_file_name(Path::new("/tmp/a b.dat"), "na/me:x"),
            "a b.dat_na_me_x.bin"
        );
    }

    #[tokio::test]
    async fn scan_start_emits_events_and_cancel_stops_job() {
        let tmp = tempfile::TempDir::new().unwrap();
        std::fs::write(tmp.path().join("f.txt"), b"x").unwrap();
        let (tx, mut rx) = mpsc::channel::<String>(64);
        let state = Arc::new(ServerState {
            backend: Arc::new(FakeBackend::new()),
            outbox: tx,
            jobs: Arc::new(Mutex::new(HashMap::new())),
        });

        let resp = handle_request(
            &state,
            &req(
                json!(5),
                "scan.start",
                json!({"path": tmp.path().to_string_lossy(), "recursive": true}),
            ),
        )
        .await
        .unwrap();
        let job_id = resp["result"]["job_id"].as_str().unwrap().to_string();
        assert!(job_id.starts_with("scan-"));

        // Wait until the completed event arrives on the outbox.
        let mut saw_completed = false;
        for _ in 0..50 {
            let Ok(line) = tokio::time::timeout(Duration::from_millis(100), rx.recv()).await else {
                continue;
            };
            let Some(line) = line else { break };
            let v: Value = serde_json::from_str(&line).unwrap();
            if v["method"] == "scan.completed" {
                assert_eq!(v["params"]["job_id"], job_id.as_str());
                saw_completed = true;
                break;
            }
        }
        assert!(saw_completed, "expected scan.completed event");

        // The finished job must be removable / already gone from the registry.
        let resp = handle_request(
            &state,
            &req(json!(6), "scan.cancel", json!({"job_id": job_id})),
        )
        .await
        .unwrap();
        assert_eq!(resp["result"]["cancelled"], false); // job already finished
    }

    #[tokio::test]
    async fn scan_cancel_unknown_job() {
        let state = test_state();
        let resp = handle_request(
            &state,
            &req(json!(9), "scan.cancel", json!({"job_id": "nope"})),
        )
        .await
        .unwrap();
        assert_eq!(resp["result"]["cancelled"], false);
    }

    #[test]
    fn error_response_shape() {
        let r = error_response(json!(1), -32601, "Method not found: x", Some(json!({"k": 1})));
        assert_eq!(r["jsonrpc"], "2.0");
        assert_eq!(r["id"], json!(1));
        assert_eq!(r["error"]["code"], -32601);
        assert_eq!(r["error"]["data"]["k"], 1);
    }
}
