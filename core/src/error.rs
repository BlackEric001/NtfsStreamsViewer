//! Structured error types for the core library.
//!
//! All errors cross IPC boundaries as structured data — never panics.

use thiserror::Error;

#[derive(Error, Debug)]
pub enum CoreError {
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Access denied: {0}")]
    AccessDenied(String),

    #[error("Path not found: {0}")]
    NotFound(String),

    #[error("Not supported on this platform: {0}")]
    NotSupported(String),

    #[error("Stream not found: {0}:{1}")]
    StreamNotFound(String, String),

    #[error("Invalid path: {0}")]
    InvalidPath(String),

    #[error("Volume is read-only: {0}")]
    VolumeReadOnly(String),

    #[error("Operation cancelled")]
    Cancelled,
}

impl CoreError {
    /// JSON-RPC error code mapping (see SPEC §5.2).
    pub fn jsonrpc_code(&self) -> i32 {
        match self {
            CoreError::AccessDenied(_) => -32001,
            CoreError::NotFound(_) | CoreError::StreamNotFound(_, _) => -32002,
            CoreError::NotSupported(_) | CoreError::InvalidPath(_) => -32003,
            CoreError::VolumeReadOnly(_) => -32004,
            CoreError::Cancelled => -32005,
            _ => -32603,
        }
    }
}

/// Convenience result alias.
pub type CoreResult<T> = Result<T, CoreError>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn jsonrpc_codes_follow_spec() {
        assert_eq!(CoreError::AccessDenied("x".into()).jsonrpc_code(), -32001);
        assert_eq!(CoreError::NotFound("x".into()).jsonrpc_code(), -32002);
        assert_eq!(
            CoreError::StreamNotFound("f".into(), "s".into()).jsonrpc_code(),
            -32002
        );
        assert_eq!(CoreError::NotSupported("x".into()).jsonrpc_code(), -32003);
        assert_eq!(CoreError::InvalidPath("x".into()).jsonrpc_code(), -32003);
        assert_eq!(CoreError::VolumeReadOnly("x".into()).jsonrpc_code(), -32004);
        assert_eq!(CoreError::Cancelled.jsonrpc_code(), -32005);
        let io = CoreError::Io(std::io::Error::other("boom"));
        assert_eq!(io.jsonrpc_code(), -32603);
    }

    #[test]
    fn display_messages() {
        assert_eq!(
            CoreError::StreamNotFound("/tmp/a.txt".into(), "Zone.Id".into()).to_string(),
            "Stream not found: /tmp/a.txt:Zone.Id"
        );
        assert_eq!(CoreError::Cancelled.to_string(), "Operation cancelled");
    }
}
