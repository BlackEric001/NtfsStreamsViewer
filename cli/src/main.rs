//! ads-cli — command-line interface and JSON-RPC server for the NTFS ADS core.
//!
//! Two modes (SPEC §4.2):
//! 1. Human-readable commands: `volumes`, `scan`, `streams`, `export`
//! 2. `serve --stdio` — JSON-RPC 2.0 over stdin/stdout for the GUI sidecar.

mod commands;
mod jsonrpc;

use clap::{Parser, Subcommand};

#[derive(Parser)]
#[command(name = "ads-cli", version, about = "NTFS Alternate Data Streams viewer (read/export only)")]
pub struct Cli {
    /// Log level: error | warn | info | debug | trace
    #[arg(long, default_value = "warn", global = true)]
    log_level: String,

    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand)]
pub enum Commands {
    /// List mounted volumes
    Volumes,
    /// Scan a path for alternate data streams
    Scan {
        /// Path to scan (file or directory)
        path: String,
        /// Do not recurse into subdirectories
        #[arg(long)]
        no_recursive: bool,
        /// Include hidden entries
        #[arg(long, default_value = "true")]
        include_hidden: bool,
        /// Include system entries (Windows attributes)
        #[arg(long)]
        include_system: bool,
        /// Also inspect directories themselves
        #[arg(long, default_value = "true")]
        include_directories: bool,
        /// Follow symlinks
        #[arg(long)]
        follow_symlinks: bool,
    },
    /// List streams of a file or directory
    Streams {
        path: String,
    },
    /// Export one stream (or all with --all) to a file/directory
    Export {
        /// Owner file/directory path
        path: String,
        /// Stream name (omit with --all)
        stream_name: Option<String>,
        /// Output file (`-o`) or directory with `--all`
        #[arg(short, long)]
        output: String,
        /// Export every stream of the file into <output> as a directory
        #[arg(long)]
        all: bool,
    },
    /// Start a JSON-RPC 2.0 server on stdin/stdout (for the GUI)
    Serve {
        /// Use stdio transport (the only transport in v0.1)
        #[arg(long)]
        stdio: bool,
    },
}

fn init_tracing(level: &str) {
    use tracing_subscriber::EnvFilter;
    let filter = EnvFilter::try_new(level).unwrap_or_else(|_| EnvFilter::new("warn"));
    // Logging must go to stderr only — stdout is reserved for JSON-RPC.
    tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_writer(std::io::stderr)
        .init();
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let cli = Cli::parse();
    init_tracing(&cli.log_level);

    match cli.command {
        Commands::Volumes => commands::volumes::run().await,
        Commands::Scan {
            path,
            no_recursive,
            include_hidden,
            include_system,
            include_directories,
            follow_symlinks,
        } => {
            let opts = ntfs_streams_core::ScanOptions {
                recursive: !no_recursive,
                include_hidden,
                include_system,
                include_directories,
                follow_symlinks,
            };
            commands::scan::run(&path, opts).await
        }
        Commands::Streams { path } => commands::streams::run(&path).await,
        Commands::Export {
            path,
            stream_name,
            output,
            all,
        } => commands::export::run(&path, stream_name.as_deref(), &output, all).await,
        Commands::Serve { stdio } => {
            if !stdio {
                anyhow::bail!("only `serve --stdio` is supported in v0.1");
            }
            jsonrpc::serve_stdio().await
        }
    }
}
