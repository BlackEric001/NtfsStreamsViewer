//! Windows backend using the native NTFS ADS APIs:
//! - `GetLogicalDriveStringsW` / `GetDriveTypeW` / `GetVolumeInformationW` /
//!   `GetDiskFreeSpaceExW` for volume enumeration,
//! - `FindFirstStreamW` / `FindNextStreamW` with `WIN32_FIND_STREAM_DATA`
//!   (`STREAM_INFO_LEVELS::FindStreamInfoStandard`) for stream enumeration,
//! - `CreateFileW` + `SetFilePointerEx` + `ReadFile` with the
//!   `\\?\path:stream:$DATA` syntax for reading/exporting.

use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_ACCESS_DENIED, ERROR_FILE_NOT_FOUND, ERROR_HANDLE_EOF,
    ERROR_NO_MORE_FILES, ERROR_PATH_NOT_FOUND, HANDLE, INVALID_HANDLE_VALUE, WIN32_ERROR,
};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FindFirstStreamW, FindNextStreamW, GetDiskFreeSpaceExW, GetLogicalDriveStringsW,
    GetVolumeInformationW, ReadFile, SetFilePointerEx, FILE_ATTRIBUTE_NORMAL,
    FILE_ATTRIBUTE_SEQUENTIAL_SCAN, FILE_BEGIN, FILE_CREATION_DISPOSITION,
    FILE_FLAGS_AND_ATTRIBUTES, FILE_GENERIC_READ, FILE_SHARE_DELETE, FILE_SHARE_MODE,
    FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING, STREAM_INFO_LEVELS, WIN32_FIND_STREAM_DATA,
};
use windows::Win32::System::WindowsProgramming::{GetDriveTypeW, DRIVE_FIXED, DRIVE_REMOVABLE};

/// Copy block size used for exports (SPEC §3.3: 64 KiB – 1 MiB).
const BLOCK_SIZE: usize = 256 * 1024;

#[derive(Clone, Default)]
pub struct WindowsBackend {}

impl WindowsBackend {
    pub fn new() -> std::sync::Arc<Self> {
        std::sync::Arc::new(Self::default())
    }
}

fn to_wide(s: &str) -> Vec<u16> {
    let mut v: Vec<u16> = s.encode_utf16().collect();
    v.push(0);
    v
}

fn path_to_wide(path: &Path) -> Vec<u16> {
    to_wide(&path.to_string_lossy())
}

/// Convert a Win32 error code into a structured [`CoreError`].
fn win_error(err: WIN32_ERROR, context: &str) -> crate::error::CoreError {
    use crate::error::CoreError;
    match err {
        ERROR_ACCESS_DENIED => CoreError::AccessDenied(context.to_string()),
        ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => CoreError::NotFound(context.to_string()),
        other => {
            let code = other.0;
            CoreError::Io(std::io::Error::from_raw_os_error(code as i32))
        }
    }
}

/// Build the extended-length (`\\?\`) path for a specific stream.
///
/// `stream_name == ""` means the default `::$DATA` stream.
pub(crate) fn build_stream_path(path: &Path, stream_name: &str) -> String {
    let raw = path.to_string_lossy().replace('/', "\\");
    let prefixed = if raw.starts_with("\\\\?\\") {
        raw
    } else if raw.starts_with("\\\\") {
        // UNC: \\server\share → \\?\UNC\server\share
        format!("\\\\?\\UNC{}", &raw[1..])
    } else {
        format!("\\\\?\\{raw}")
    };
    if stream_name.is_empty() {
        prefixed
    } else {
        format!("{prefixed}:{stream_name}:$DATA")
    }
}

unsafe fn open_stream_read(path: &Path, stream_name: &str) -> Result<HANDLE, crate::error::CoreError> {
    let full = build_stream_path(path, stream_name);
    let wide = to_wide(&full);
    // FILE_ATTRIBUTE_SEQUENTIAL_SCAN == FILE_FLAG_SEQUENTIAL_SCAN (0x10000000)
    // and already includes FILE_ATTRIBUTE_NORMAL.
    let share = FILE_SHARE_MODE(FILE_SHARE_READ.0 | FILE_SHARE_WRITE.0 | FILE_SHARE_DELETE.0);
    let flags = FILE_ATTRIBUTE_SEQUENTIAL_SCAN;
    let disposition = OPEN_EXISTING;
    let handle = CreateFileW(
        PCWSTR(wide.as_ptr()),
        FILE_GENERIC_READ.0,
        share,
        None,
        disposition,
        flags,
        HANDLE::default(),
    );
    match handle {
        Ok(h) if h != INVALID_HANDLE_VALUE => Ok(h),
        Ok(_) | Err(_) => {
            let err = GetLastError();
            Err(win_error(err, &path.display().to_string()))
        }
    }
}

/// Parse a `WIN32_FIND_STREAM_DATA.cStreamName` value of the form
/// `":Name:$TYPE"` (or `"::$DATA"` for the default stream).
fn parse_stream_name(raw: &str) -> Option<(String, String)> {
    let rest = raw.strip_prefix(':')?;
    let (name, ty) = rest.rsplit_once(':')?;
    Some((name.to_string(), ty.to_string()))
}

fn from_wide(buf: &[u16]) -> String {
    let end = buf.iter().position(|c| *c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..end])
}

impl WindowsBackend {
    pub(crate) fn list_volumes_sync(&self) -> Result<Vec<crate::types::VolumeInfo>, crate::error::CoreError> {
        use crate::types::VolumeInfo;
        unsafe {
            let mut buf = vec![0u16; 512];
            let len = GetLogicalDriveStringsW(Some(&mut buf));
            if len == 0 {
                return Ok(Vec::new());
            }
            let drives = parse_double_null_terminated(&buf[..len as usize]);
            let mut volumes = Vec::new();
            for drive in &drives {
                // e.g. "C:\"
                let wide = to_wide(drive);
                let drive_type = GetDriveTypeW(PCWSTR(wide.as_ptr()));
                let is_removable = drive_type == DRIVE_REMOVABLE;
                if drive_type != DRIVE_FIXED && !is_removable {
                    continue;
                }
                let mut fs_name = [0u16; 64];
                let mut vol_name = [0u16; 128];
                let mut serial = 0u32;
                let mut max_component = 0u32;
                let mut flags = 0u32;
                let ok = GetVolumeInformationW(
                    PCWSTR(wide.as_ptr()),
                    Some(&mut vol_name),
                    Some(&mut serial),
                    Some(&mut max_component),
                    Some(&mut flags),
                    Some(&mut fs_name),
                );
                let file_system = if ok.is_ok() {
                    from_wide(&fs_name)
                } else {
                    String::new()
                };
                let is_ntfs = file_system.eq_ignore_ascii_case("NTFS");
                let mut free_available = 0u64;
                let mut total = 0u64;
                let mut total_free = 0u64;
                let (total_size, free_size) = if GetDiskFreeSpaceExW(
                    PCWSTR(wide.as_ptr()),
                    Some(&mut free_available),
                    Some(&mut total),
                    Some(&mut total_free),
                )
                .is_ok()
                {
                    (Some(total), Some(free_available))
                } else {
                    (None, None)
                };
                const FILE_READ_ONLY_VOLUME_VAL: u32 = 0x0008_0000;
                let writable = (flags & FILE_READ_ONLY_VOLUME_VAL) == 0;
                let name_trimmed = from_wide(&vol_name);
                volumes.push(VolumeInfo {
                    id: drive.trim_end_matches(['\\', '/']).to_string(),
                    name: if name_trimmed.is_empty() {
                        drive.clone()
                    } else {
                        format!("{name_trimmed} ({drive})")
                    },
                    mount_point: PathBuf::from(drive),
                    file_system,
                    is_ntfs,
                    is_writable: writable,
                    total_size,
                    free_size,
                });
            }
            Ok(volumes)
        }
    }

    pub(crate) fn list_streams_sync(
        &self,
        path: &Path,
    ) -> Result<Vec<crate::types::StreamInfo>, crate::error::CoreError> {
        use crate::types::StreamInfo;
        unsafe {
            let wide = path_to_wide(path);
            let mut data = WIN32_FIND_STREAM_DATA::default();
            let find = FindFirstStreamW(
                PCWSTR(wide.as_ptr()),
                STREAM_INFO_LEVELS::FindStreamInfoStandard,
                &mut data as *mut _ as *mut c_void,
                0,
            );
            let find = match find {
                Ok(h) => h,
                Err(_) => {
                    // `FindFirstStreamW` reports "no streams" / missing file via
                    // the last-error code; consult it explicitly.
                    let err = GetLastError();
                    if err == ERROR_NO_MORE_FILES || err == ERROR_HANDLE_EOF {
                        return Ok(Vec::new());
                    }
                    return Err(win_error(err, &path.display().to_string()));
                }
            };
            let mut streams = Vec::new();
            loop {
                let raw = from_wide(&data.cStreamName);
                if let Some((name, ty)) = parse_stream_name(&raw) {
                    // Only $DATA streams; the default "::\$DATA" has an empty name.
                    if ty.eq_ignore_ascii_case("$DATA") && !name.is_empty() {
                        streams.push(StreamInfo {
                            name,
                            size: data.StreamSize.max(0) as u64,
                            owner_path: path.to_path_buf(),
                        });
                    }
                }
                if FindNextStreamW(find, &mut data as *mut _ as *mut c_void).is_err() {
                    let err = GetLastError();
                    if err == ERROR_HANDLE_EOF || err == ERROR_NO_MORE_FILES {
                        break;
                    }
                    let _ = CloseHandle(find);
                    return Err(win_error(err, &path.display().to_string()));
                }
            }
            let _ = CloseHandle(find);
            streams.sort_by(|a, b| a.name.cmp(&b.name));
            Ok(streams)
        }
    }

    pub(crate) fn read_stream_sync(
        &self,
        path: &Path,
        stream_name: &str,
        offset: u64,
        len: usize,
    ) -> Result<(Vec<u8>, bool), crate::error::CoreError> {
        unsafe {
            let handle = open_stream_read(path, stream_name)?;
            let seek = SetFilePointerEx(
                handle,
                offset.min(i64::MAX as u64) as i64,
                None,
                FILE_BEGIN,
            );
            if seek.is_err() {
                let err = GetLastError();
                let _ = CloseHandle(handle);
                return Err(win_error(err, &path.display().to_string()));
            }
            let mut total = Vec::with_capacity(len.min(BLOCK_SIZE));
            let mut buf = vec![0u8; BLOCK_SIZE];
            while total.len() < len {
                let want = (len - total.len()).min(BLOCK_SIZE);
                let mut read = 0u32;
                if let Err(_) = ReadFile(handle, Some(&mut buf[..want]), Some(&mut read), None) {
                    // Reading past EOF on some streams surfaces as ERROR_HANDLE_EOF.
                    let err = GetLastError();
                    if err == ERROR_HANDLE_EOF {
                        break;
                    }
                    let _ = CloseHandle(handle);
                    return Err(win_error(err, &path.display().to_string()));
                }
                if read == 0 {
                    break;
                }
                total.extend_from_slice(&buf[..read as usize]);
            }
            let _ = CloseHandle(handle);
            // EOF when we couldn't fill the requested length.
            let eof = total.len() < len;
            Ok((total, eof))
        }
    }

    pub(crate) fn export_stream_sync(
        &self,
        path: &Path,
        stream_name: &str,
        destination: &Path,
        cancel: &CancellationToken,
    ) -> Result<u64, crate::error::CoreError> {
        use crate::error::CoreError;
        if cancel.is_cancelled() {
            return Err(CoreError::Cancelled);
        }
        if let Some(dir) = destination.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let mut out = std::fs::File::create(destination)?;
        let written = self.copy_stream_to(path, stream_name, &mut out, cancel)?;
        Ok(written)
    }

    fn copy_stream_to(
        &self,
        path: &Path,
        stream_name: &str,
        out: &mut dyn std::io::Write,
        cancel: &CancellationToken,
    ) -> Result<u64, crate::error::CoreError> {
        let mut written = 0u64;
        loop {
            if cancel.is_cancelled() {
                return Err(crate::error::CoreError::Cancelled);
            }
            let (chunk, eof) = self.read_stream_sync(path, stream_name, written, BLOCK_SIZE)?;
            if chunk.is_empty() {
                return Ok(written);
            }
            out.write_all(&chunk)?;
            written += chunk.len() as u64;
            if eof {
                return Ok(written);
            }
        }
    }
}

#[async_trait]
impl crate::backend::AdsBackend for WindowsBackend {
    async fn list_volumes(&self) -> Result<Vec<crate::types::VolumeInfo>, crate::error::CoreError> {
        let this = self.clone();
        tokio::task::spawn_blocking(move || this.list_volumes_sync())
            .await
            .map_err(|e| crate::error::CoreError::Io(std::io::Error::other(e.to_string())))?
    }

    async fn list_streams(
        &self,
        path: &Path,
    ) -> Result<Vec<crate::types::StreamInfo>, crate::error::CoreError> {
        let this = self.clone();
        let path = path.to_path_buf();
        tokio::task::spawn_blocking(move || this.list_streams_sync(&path))
            .await
            .map_err(|e| crate::error::CoreError::Io(std::io::Error::other(e.to_string())))?
    }

    async fn read_stream(
        &self,
        path: &Path,
        stream_name: &str,
        offset: u64,
        len: usize,
    ) -> Result<(Vec<u8>, bool), crate::error::CoreError> {
        let this = self.clone();
        let path = path.to_path_buf();
        let stream_name = stream_name.to_string();
        tokio::task::spawn_blocking(move || this.read_stream_sync(&path, &stream_name, offset, len))
            .await
            .map_err(|e| crate::error::CoreError::Io(std::io::Error::other(e.to_string())))?
    }

    async fn export_stream(
        &self,
        path: &Path,
        stream_name: &str,
        destination: &Path,
        cancel: CancellationToken,
    ) -> Result<u64, crate::error::CoreError> {
        let this = self.clone();
        let path = path.to_path_buf();
        let stream_name = stream_name.to_string();
        let destination = destination.to_path_buf();
        tokio::task::spawn_blocking(move || {
            this.export_stream_sync(&path, &stream_name, &destination, &cancel)
        })
        .await
        .map_err(|e| crate::error::CoreError::Io(std::io::Error::other(e.to_string())))?
    }
}

fn trim_nul(s: &[u16]) -> &[u16] {
    let end = s.iter().position(|c| *c == 0).unwrap_or(s.len());
    &s[..end]
}

fn parse_double_null_terminated(buf: &[u16]) -> Vec<String> {
    let mut out = Vec::new();
    let mut current = String::new();
    for &c in buf {
        if c == 0 {
            if !current.is_empty() {
                out.push(std::mem::take(&mut current));
            }
        } else {
            current.push(char::from_u32(c as u32).unwrap_or('\u{fffd}'));
        }
    }
    if !current.is_empty() {
        out.push(current);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_build_stream_path() {
        let p = Path::new("C:\\data\\file.txt");
        assert_eq!(
            build_stream_path(p, "Zone.Identifier"),
            "\\\\?\\C:\\data\\file.txt:Zone.Identifier:$DATA"
        );
        assert_eq!(build_stream_path(p, ""), "\\\\?\\C:\\data\\file.txt");
    }

    #[test]
    fn test_build_stream_path_unc() {
        let p = Path::new("\\\\server\\share\\file.txt");
        assert_eq!(
            build_stream_path(p, "mystream"),
            "\\\\?\\UNC\\server\\share\\file.txt:mystream:$DATA"
        );
    }

    #[test]
    fn test_parse_double_null() {
        let mut buf = Vec::new();
        for s in ["C:\\", "D:\\"] {
            buf.extend(s.encode_utf16());
            buf.push(0);
        }
        buf.push(0);
        let v = parse_double_null_terminated(&buf);
        assert_eq!(v, vec!["C:\\", "D:\\"]);
    }

    #[test]
    fn test_parse_stream_name() {
        assert_eq!(
            parse_stream_name(":Zone.Identifier:$DATA"),
            Some(("Zone.Identifier".into(), "$DATA".into()))
        );
        assert_eq!(
            parse_stream_name("::$DATA"),
            Some(("".into(), "$DATA".into()))
        );
        assert_eq!(parse_stream_name(":Foo:$X"), Some(("Foo".into(), "$X".into())));
        assert_eq!(parse_stream_name("garbage"), None);
    }

    #[test]
    fn test_trim_nul() {
        let w = to_wide("abc");
        assert_eq!(trim_nul(&w), [b'a' as u16, b'b' as u16, b'c' as u16]);
    }
}
