# NtfsStreamsViewer

NTFS Alternate Data Streams viewer and editor for Windows and Linux.

## Features

- Scan mounted NTFS volumes for alternate data streams
- View stream contents (hex, text)
- Export streams to files
- Cross-platform support (Windows/Linux)
- Create and delete streams (planned)

## Architecture

This project consists of three main components:

- **core**: Rust library for working with NTFS streams on mounted volumes
- **cli**: Rust command-line interface and JSON-RPC server
- **gui**: Avalonia/C# desktop application

## Building

### Prerequisites

- Rust (latest stable)
- .NET 8.0 SDK

### Build Components

**Core library:**
```bash
cd core
cargo build --release


cd cli
cargo build --release


cd gui
dotnet publish -c Release
bash
```

### Usage
**CLI Mode**

# List mounted NTFS volumes
ads-cli volumes

# Scan a directory
ads-cli scan /mnt/windows/data

# List streams for a file
ads-cli streams /mnt/windows/data/file.txt

# Export a stream
ads-cli export /mnt/windows/data/file.txt Zone.Identifier -o output.bin

# Start JSON-RPC server (for GUI)
ads-cli serve --stdio
