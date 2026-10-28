//! Linux backend: NTFS alternate data streams are exposed by the `ntfs3`
//! kernel driver and `ntfs-3g` FUSE driver as extended attributes with the
//! `user.` prefix (`user.StreamName` ↔ ADS `StreamName:$DATA`).
//!
//! Per SPEC we only treat a path as having ADS if it lives on an NTFS mount
//! (`ntfs`, `ntfs3`, or `fuseblk` mounted from an NTFS device).

use std::path::{Path, PathBuf};
use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::backend::AdsBackend;
use crate::error::CoreError;
use crate::types::{StreamInfo, VolumeInfo};

/// xattr namespace prefix used by ntfs-3g / ntfs3 for named DATA streams.
const USER_PREFIX: &str = "user.";
/// Special ntfs-3g xattrs that emulate NTFS metadata rather than representing
/// real named ADS — skip them.
const SKIP_XATTRS: &[&str] = &[
    "user.ntfs_dos_name",
    "user.ntfs_reparse_data",
    "user.ntfs_file_name",
    "user.ntfs_sec.hash",
];

#[derive(Default)]
struct Inner {
    /// Cache: mount point -> is NTFS.
    ntfs_cache: std::collections::HashMap<PathBuf, bool>,
}

#[derive(Default)]
pub struct LinuxBackend {
    inner: Arc<std::sync::Mutex<Inner>>,
}

impl LinuxBackend {
    pub fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    fn clone_inner(&self) -> Self {
        Self {
            inner: self.inner.clone(),
        }
    }

    /// Determine whether `path` resides on an NTFS mount by consulting
    /// `/proc/self/mountinfo` (longest matching mount point wins).
    fn detect_ntfs(path: &Path) -> bool {
        let mounts = match parse_mountinfo() {
            Ok(m) => m,
            Err(_) => return false,
        };
        let mut best: Option<(usize, bool)> = None;
        for m in &mounts {
            if path.starts_with(&m.mount_point) {
                let len = m.mount_point.as_os_str().len();
                if best.map(|(l, _)| len >= l).unwrap_or(true) {
                    best = Some((len, m.is_ntfs()));
                }
            }
        }
        best.map(|(_, v)| v).unwrap_or(false)
    }

    fn check_ntfs(&self, path: &Path) -> Result<(), CoreError> {
        let abs = absolutize(path);
        {
            let cache = self.inner.lock().unwrap();
            let mut key = abs.clone();
            loop {
                if let Some(&is_ntfs) = cache.ntfs_cache.get(&key) {
                    return if is_ntfs { Ok(()) } else { Err(not_ntfs(&abs)) };
                }
                if !key.pop() {
                    break;
                }
            }
        }
        let is_ntfs = Self::detect_ntfs(&abs);
        {
            let mut cache = self.inner.lock().unwrap();
            if let Ok(mounts) = parse_mountinfo() {
                for m in mounts {
                    if abs.starts_with(&m.mount_point) {
                        cache.ntfs_cache.insert(m.mount_point, is_ntfs);
                        break;
                    }
                }
            }
        }
        if is_ntfs {
            Ok(())
        } else {
            Err(not_ntfs(&abs))
        }
    }

    fn list_volumes_sync(&self) -> Result<Vec<VolumeInfo>, CoreError> {
        let mounts = parse_mountinfo()?;
        let mut volumes = Vec::new();
        let mut seen = std::collections::HashSet::new();
        for (idx, m) in mounts.iter().enumerate() {
            if !seen.insert(m.mount_point.clone()) {
                continue; // bind mounts repeat the same mount point
            }
            let is_ntfs = m.is_ntfs();
            let (total, free) = statvfs_sizes(&m.mount_point);
            let writable = probe_writable(&m.mount_point);
            let name = m
                .mount_point
                .file_name()
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_else(|| m.mount_point.to_string_lossy().into_owned());
            volumes.push(VolumeInfo {
                id: format!("vol{idx}"),
                name,
                mount_point: m.mount_point.clone(),
                file_system: m.fs_type.clone(),
                is_ntfs,
                is_writable: writable,
                total_size: total,
                free_size: free,
            });
        }
        Ok(volumes)
    }

    fn list_streams_sync(&self, path: &Path) -> Result<Vec<StreamInfo>, CoreError> {
        if !path.exists() {
            return Err(CoreError::NotFound(path.display().to_string()));
        }
        self.check_ntfs(path)?;

        let names = xattr::list(path).map_err(|e| classify_xattr_error(e, path))?;
        let mut streams = Vec::new();
        for attr in names {
            let attr = attr.to_string_lossy().into_owned();
            let Some(stream_name) = attr.strip_prefix(USER_PREFIX) else {
                continue;
            };
            if SKIP_XATTRS.contains(&attr.as_str()) || stream_name.is_empty() {
                continue;
            }
            let size = match xattr::get(path, &attr) {
                Ok(Some(data)) => data.len() as u64,
                _ => 0,
            };
            streams.push(StreamInfo {
                name: stream_name.to_string(),
                size,
                owner_path: path.to_path_buf(),
            });
        }
        streams.sort_by(|a, b| a.name.cmp(&b.name));
        Ok(streams)
    }

    fn read_stream_sync(
        &self,
        path: &Path,
        stream_name: &str,
        offset: u64,
        len: usize,
    ) -> Result<(Vec<u8>, bool), CoreError> {
        if !path.exists() {
            return Err(CoreError::NotFound(path.display().to_string()));
        }
        self.check_ntfs(path)?;
        let attr = format!("{USER_PREFIX}{stream_name}");
        let data = xattr::get(path, &attr)
            .map_err(|e| classify_xattr_error(e, path))?
            .ok_or_else(|| {
                CoreError::StreamNotFound(path.display().to_string(), stream_name.into())
            })?;
        let off = offset as usize;
        if off >= data.len() {
            return Ok((Vec::new(), true));
        }
        let end = (off + len).min(data.len());
        let eof = end >= data.len();
        Ok((data[off..end].to_vec(), eof))
    }

    fn export_stream_sync(
        &self,
        path: &Path,
        stream_name: &str,
        destination: &Path,
        cancel: &CancellationToken,
    ) -> Result<u64, CoreError> {
        let (data, _) = self.read_stream_sync(path, stream_name, 0, usize::MAX)?;
        if cancel.is_cancelled() {
            return Err(CoreError::Cancelled);
        }
        if let Some(dir) = destination.parent() {
            std::fs::create_dir_all(dir)?;
        }
        std::fs::write(destination, &data)?;
        Ok(data.len() as u64)
    }
}

fn not_ntfs(abs: &Path) -> CoreError {
    CoreError::NotSupported(format!("{} is not on an NTFS volume", abs.display()))
}

fn absolutize(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|c| c.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    }
}

struct MountEntry {
    mount_point: PathBuf,
    fs_type: String,
    device: String,
}

impl MountEntry {
    fn is_ntfs(&self) -> bool {
        match self.fs_type.as_str() {
            "ntfs" | "ntfs3" => true,
            // fuseblk could be ntfs-3g; confirm via superblock magic when the
            // block device is readable.
            "fuseblk" => read_fs_magic(&self.device).as_deref() == Some("NTFS"),
            _ => false,
        }
    }
}

/// Read filesystem magic from a block device (best effort, needs permissions).
fn read_fs_magic(device: &str) -> Option<String> {
    use std::io::{Read, Seek};
    if !device.starts_with("/dev/") {
        return None;
    }
    let mut f = std::fs::File::open(device).ok()?;
    let mut buf = [0u8; 8];
    // NTFS OEM ID "NTFS    " at offset 3.
    f.seek(std::io::SeekFrom::Start(3)).ok()?;
    let n = f.read(&mut buf).ok()?;
    if n >= 5 && &buf[..5] == b"NTFS " {
        Some("NTFS".to_string())
    } else {
        None
    }
}

fn parse_mountinfo() -> Result<Vec<MountEntry>, CoreError> {
    parse_mountinfo_from("/proc/self/mountinfo")
}

fn parse_mountinfo_from(file: &str) -> Result<Vec<MountEntry>, CoreError> {
    let content = std::fs::read_to_string(file)?;
    let mut out = Vec::new();
    for line in content.lines() {
        // Format: id parent major:minor root mount_point opts fstype superopts - source ...
        let parts: Vec<&str> = line.split(' ').collect();
        let Some(sep_idx) = parts.iter().position(|p| *p == "-") else {
            continue;
        };
        if parts.len() < sep_idx + 3 || parts.len() < 6 {
            continue;
        }
        let mount_point = unescape_octal(parts[4]);
        let fstype = parts[sep_idx + 1].to_string();
        let source = parts[sep_idx + 2].to_string();
        if mount_point.is_empty() {
            continue;
        }
        out.push(MountEntry {
            mount_point: PathBuf::from(mount_point),
            fs_type: fstype,
            device: source,
        });
    }
    Ok(out)
}

/// mountinfo escapes spaces/tabs/newlines/backslashes as \040 etc.
fn unescape_octal(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        // Need a full three-digit octal escape: \ + 3 digits (e.g. "\040").
        if bytes[i] == b'\\' && i + 3 < bytes.len() && bytes[i + 1..i + 4].iter().all(u8::is_ascii_digit) {
            if let Ok(v) = u8::from_str_radix(&s[i + 1..i + 4], 8) {
                out.push(v);
                i += 4;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

#[async_trait]
impl AdsBackend for LinuxBackend {
    async fn list_volumes(&self) -> Result<Vec<VolumeInfo>, CoreError> {
        let this = self.clone_inner();
        tokio::task::spawn_blocking(move || this.list_volumes_sync())
            .await
            .map_err(join_err)?
    }

    async fn list_streams(&self, path: &Path) -> Result<Vec<StreamInfo>, CoreError> {
        let this = self.clone_inner();
        let path = path.to_path_buf();
        tokio::task::spawn_blocking(move || this.list_streams_sync(&path))
            .await
            .map_err(join_err)?
    }

    async fn read_stream(
        &self,
        path: &Path,
        stream_name: &str,
        offset: u64,
        len: usize,
    ) -> Result<(Vec<u8>, bool), CoreError> {
        let this = self.clone_inner();
        let path = path.to_path_buf();
        let stream_name = stream_name.to_string();
        tokio::task::spawn_blocking(move || this.read_stream_sync(&path, &stream_name, offset, len))
            .await
            .map_err(join_err)?
    }

    async fn export_stream(
        &self,
        path: &Path,
        stream_name: &str,
        destination: &Path,
        cancel: CancellationToken,
    ) -> Result<u64, CoreError> {
        let this = self.clone_inner();
        let path = path.to_path_buf();
        let stream_name = stream_name.to_string();
        let destination = destination.to_path_buf();
        tokio::task::spawn_blocking(move || {
            this.export_stream_sync(&path, &stream_name, &destination, &cancel)
        })
        .await
        .map_err(join_err)?
    }
}

fn join_err(e: tokio::task::JoinError) -> CoreError {
    CoreError::Io(std::io::Error::other(e.to_string()))
}

/// Heuristic writability check: try creating a temp file.
fn probe_writable(path: &Path) -> bool {
    let probe = path.join(".ads-cli-write-probe");
    match std::fs::OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&probe)
    {
        Ok(_) => {
            let _ = std::fs::remove_file(&probe);
            true
        }
        Err(_) => false,
    }
}

fn classify_xattr_error(e: std::io::Error, path: &Path) -> CoreError {
    match e.raw_os_error() {
        Some(libc::EACCES) | Some(libc::EPERM) => {
            CoreError::AccessDenied(path.display().to_string())
        }
        Some(libc::ENOENT) => CoreError::NotFound(path.display().to_string()),
        Some(libc::ENOTSUP) | Some(libc::ERANGE) => {
            CoreError::NotSupported(format!("xattrs not supported on {}", path.display()))
        }
        _ => CoreError::Io(e),
    }
}

fn statvfs_sizes(path: &Path) -> (Option<u64>, Option<u64>) {
    use std::ffi::CString;
    use std::os::unix::ffi::OsStrExt;
    let Ok(cpath) = CString::new(path.as_os_str().as_bytes()) else {
        return (None, None);
    };
    unsafe {
        let mut stat: libc::statvfs = std::mem::zeroed();
        if libc::statvfs(cpath.as_ptr(), &mut stat) == 0 {
            let frsize = stat.f_frsize as u64;
            let total = stat.f_blocks as u64 * frsize;
            let free = stat.f_bavail as u64 * frsize;
            (Some(total), Some(free))
        } else {
            (None, None)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_unescape_octal() {
        // mountinfo escapes special chars as a backslash followed by three
        // octal digits, e.g. space -> "\040" (literal backslash in the file).
        assert_eq!(unescape_octal(r"/mnt/my\040disk"), "/mnt/my disk");
        assert_eq!(unescape_octal("/plain"), "/plain");
        assert_eq!(unescape_octal(r"tab\011sep"), "tab\tsep");
        assert_eq!(unescape_octal(r"back\134slash"), "back\\slash");
    }

    #[test]
    fn test_parse_mountinfo_from_sample() {
        let sample = "22 1 8:1 / /mnt/win rw,relatime shared:2 - ntfs3 /dev/sda1 rw,uid=0\n\
                      23 1 8:2 / /home rw - ext4 /dev/sdb2 rw\n";
        let file = "/tmp/ads_test_mountinfo";
        std::fs::write(file, sample).unwrap();
        let mounts = parse_mountinfo_from(file).unwrap();
        assert_eq!(mounts.len(), 2);
        assert_eq!(mounts[0].mount_point, PathBuf::from("/mnt/win"));
        assert_eq!(mounts[0].fs_type, "ntfs3");
        assert!(!mounts[1].is_ntfs());
        let _ = std::fs::remove_file(file);
    }

    #[test]
    fn test_non_ntfs_path_is_not_supported() {
        let be = LinuxBackend::new();
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let res = rt.block_on(be.list_streams(Path::new("/tmp")));
        // /tmp is essentially never NTFS; either NotSupported or (rare CI with
        // NTFS /tmp) Ok.
        assert!(
            matches!(res, Err(CoreError::NotSupported(_))) || res.is_ok(),
            "unexpected error: {:?}",
            res.err()
        );
    }

    #[test]
    fn test_missing_path_is_not_found() {
        let be = LinuxBackend::new();
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let res = rt.block_on(be.list_streams(Path::new("/nonexistent-ads-test-path")));
        assert!(matches!(res, Err(CoreError::NotFound(_))));
    }
}
