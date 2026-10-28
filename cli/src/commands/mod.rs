//! Human-readable CLI commands.

pub mod export;
pub mod scan;
pub mod streams;
pub mod volumes;

use std::path::Path;

/// Render a byte size human-readably (e.g. `4.0 KB`).
pub fn human_size(bytes: u64) -> String {
    const UNITS: [&str; 6] = ["B", "KB", "MB", "GB", "TB", "PB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 {
        format!("{bytes} B")
    } else {
        format!("{value:.1} {}", UNITS[unit])
    }
}

/// Sanitize a stream name for use in file names (SPEC §10).
pub fn sanitize_stream_name(name: &str) -> String {
    name.chars()
        .map(|c| match c {
            '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|' => '_',
            c if (c as u32) < 0x20 => '_',
            c => c,
        })
        .collect()
}

/// Output file name for exporting a stream: `<file>_<stream>.bin`.
pub fn export_file_name(owner: &Path, stream_name: &str) -> String {
    let base = owner
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| "owner".to_string());
    let base = sanitize_stream_name(&base);
    format!("{base}_{}.bin", sanitize_stream_name(stream_name))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_human_size() {
        assert_eq!(human_size(0), "0 B");
        assert_eq!(human_size(128), "128 B");
        assert_eq!(human_size(4096), "4.0 KB");
        assert_eq!(human_size(5 * 1024 * 1024), "5.0 MB");
    }

    #[test]
    fn test_sanitize() {
        assert_eq!(sanitize_stream_name("a/b:c"), "a_b_c");
        // On Linux the whole Windows path is a single file name component,
        // so it gets sanitized in place; on Linux paths only the base is used.
        let out = export_file_name(Path::new("/d/f.txt"), "Zone.Identifier");
        assert_eq!(out, "f.txt_Zone.Identifier.bin");
    }

    #[test]
    fn test_export_file_name_strips_directories() {
        let out = export_file_name(Path::new("/tmp/my file.dat"), "prompt:foo\x01bar");
        assert_eq!(out, "my file.dat_prompt_foo_bar.bin");
    }
}
