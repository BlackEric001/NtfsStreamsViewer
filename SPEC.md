# NtfsStreamsViewer — Техническое задание

## 1. Контекст проекта

Создать кроссплатформенное приложение для просмотра и управления NTFS Alternate Data Streams (ADS) на **примонтированных** томах. Приложение должно работать под Windows и Linux.

**Первая версия (v0.1):**
- Только чтение и экспорт (без создания/удаления потоков)
- Только примонтированные тома (без работы с raw disk/MFT)
- Безопасный режим: подтверждение для опасных операций

## 2. Архитектура

### 2.1. Компоненты

Проект состоит из трех компонентов:

1. **core** (Rust библиотека) — логика работы с NTFS потоками
2. **cli** (Rust бинарник) — CLI интерфейс и JSON-RPC сервер
3. **gui** (Avalonia/C#) — десктопное приложение

### 2.2. Принципы

- Core библиотека не знает про консоль, JSON, GUI
- CLI использует core как зависимость
- GUI запускает CLI как отдельный процесс (`ads-cli serve --stdio`)
- Обмен данными через JSON-RPC 2.0 over stdin/stdout
- Core абстрагирует платформенные различия через trait `AdsBackend`
- Логика не смешивается с интерфейсами

### 2.3. Структура репозитория

```
NtfsStreamsViewer/
├── Cargo.toml                   # Workspace
├── README.md
├── SPEC.md                      # Этот документ
│
├── core/                        # Rust библиотека
│   ├── Cargo.toml
│   └── src/
│       ├── lib.rs
│       ├── error.rs
│       ├── types.rs
│       ├── backend.rs
│       └── backends/
│           ├── mod.rs
│           ├── windows.rs
│           └── linux.rs
│
├── cli/                         # Rust CLI
│   ├── Cargo.toml
│   └── src/
│       ├── main.rs
│       ├── jsonrpc.rs
│       └── commands/
│           ├── mod.rs
│           ├── volumes.rs
│           ├── scan.rs
│           ├── streams.rs
│           ├── export.rs
│           └── serve.rs
│
├── gui/                         # Avalonia C#
│   ├── NtfsStreamsViewer.sln
│   └── src/
│       ├── App.axaml
│       ├── App.axaml.cs
│       ├── Views/
│       │   ├── MainWindow.axaml
│       │   ├── MainWindow.axaml.cs
│       │   └── StreamPreviewWindow.axaml
│       ├── ViewModels/
│       │   ├── MainViewModel.cs
│       │   ├── ScanViewModel.cs
│       │   └── StreamPreviewViewModel.cs
│       └── Services/
│           ├── CoreClient.cs
│           ├── CoreProcessService.cs
│           └── JsonRpcService.cs
│
├── protocol/
│   └── schema.json
└── docs/
    ├── architecture.md
    └── user-guide.md
```

## 3. Core библиотека (Rust)

### 3.1. Зависимости

```toml
[dependencies]
thiserror = "1.0"
serde = { version = "1.0", features = ["derive"] }
serde_json = "1.0"
tokio = { version = "1.0", features = ["full"] }
anyhow = "1.0"
tracing = "0.1"
tokio-util = "0.7"

[target.'cfg(windows)'.dependencies]
windows = { version = "0.58", features = [
    "Win32_Storage_FileSystem",
    "Win32_Foundation",
    "Win32_System_IO",
    "Win32_Security",
    "Win32_System_SystemInformation"
] }

[target.'cfg(unix)'.dependencies]
xattr = "1.3"
libc = "0.2"
```

### 3.2. Модули

#### `error.rs` — Типы ошибок

```rust
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
```

#### `types.rs` — Доменные типы

```rust
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VolumeInfo {
    pub id: String,
    pub name: String,
    pub mount_point: PathBuf,
    pub file_system: String,
    pub is_ntfs: bool,
    pub is_writable: bool,
    pub total_size: Option<u64>,
    pub free_size: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StreamInfo {
    pub name: String,
    pub size: u64,
    pub owner_path: PathBuf,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ScanOptions {
    pub recursive: bool,
    pub include_hidden: bool,
    pub include_system: bool,
    pub include_directories: bool,
    pub follow_symlinks: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ScanProgress {
    pub files_scanned: u64,
    pub dirs_scanned: u64,
    pub streams_found: u64,
    pub errors: u64,
    pub current_path: Option<PathBuf>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ScanResult {
    pub job_id: String,
    pub total_files: u64,
    pub total_streams: u64,
    pub errors: Vec<String>,
    pub duration_ms: u64,
}
```

#### `backend.rs` — Абстракция бэкенда

```rust
use crate::{CoreError, StreamInfo, VolumeInfo};
use std::path::Path;
use tokio_util::sync::CancellationToken;

#[async_trait::async_trait]
pub trait AdsBackend: Send + Sync {
    /// Получить список доступных примонтированных NTFS томов
    async fn list_volumes(&self) -> Result<Vec<VolumeInfo>, CoreError>;

    /// Получить список потоков для конкретного файла или папки
    async fn list_streams(&self, path: &Path) -> Result<Vec<StreamInfo>, CoreError>;

    /// Прочитать часть потока
    async fn read_stream(
        &self,
        path: &Path,
        stream_name: &str,
        offset: u64,
        len: usize,
    ) -> Result<Vec<u8>, CoreError>;

    /// Экспортировать поток в файл
    async fn export_stream(
        &self,
        path: &Path,
        stream_name: &str,
        destination: &Path,
        cancel: CancellationToken,
    ) -> Result<u64, CoreError>;
}
```

### 3.3. Реализация Windows Backend

#### Перечисление томов

Использовать:
- `GetLogicalDriveStringsW` для получения списка дисков
- `GetDriveTypeW` для определения типа
- `GetVolumeInformationW` для определения файловой системы
- `GetDiskFreeSpaceExW` для информации о размере

#### Перечисление потоков

Использовать:
- `FindFirstStreamW` / `FindNextStreamW` с уровнем `FindStreamInfoStandard`
- Обрабатывать `ERROR_HANDLE_EOF` как "потоков нет"
- Извлекать имя потока из формата `:StreamName:$DATA` (отбрасывать `::$DATA` как основной)
- Обрабатывать как файлы, так и папки

#### Чтение/экспорт потоков

Использовать:
- `CreateFileW` с путем формата `\\?\C:\path\file.txt:StreamName:$DATA`
- Для длинных путей обязательно использовать префикс `\\?\`
- Чтение блоками по 64 KiB — 1 MiB
- Экспорт через копирование блоков в целевой файл

#### Обработка ошибок

- `ERROR_ACCESS_DENIED` → `CoreError::AccessDenied`
- `ERROR_FILE_NOT_FOUND` → `CoreError::NotFound`
- `ERROR_HANDLE_EOF` → пустой список потоков (не ошибка)

### 3.4. Реализация Linux Backend

#### Определение NTFS

Использовать:
- `statfs` или чтение `/proc/self/mountinfo` для определения FS
- Проверять тип ФС: `ntfs`, `ntfs3`, `fuseblk` (для ntfs-3g)

#### Перечисление потоков

Использовать:
- `listxattr` для получения списка атрибутов
- Фильтровать по префиксу `user.*` (но только для NTFS!)
- Маппить `user.StreamName` → `StreamName`
- Для размера: `getxattr` и проверять длину данных

#### Чтение потоков

Использовать:
- `getxattr(path, "user.StreamName", buffer)` для чтения
- Обрабатывать ограничение размера xattr

#### Экспорт потоков

Читать через `getxattr` и записывать в файл.

## 4. CLI (Rust)

### 4.1. Зависимости

```toml
[dependencies]
clap = { version = "4.0", features = ["derive"] }
ntfs-streams-core = { path = "../core" }
tokio = { workspace = true }
serde_json = { workspace = true }
tracing = "0.1"
tracing-subscriber = "0.3"
```

### 4.2. Режимы работы

#### Режим 1: Человекочитаемый CLI

```bash
ads-cli volumes
ads-cli scan <path> [--recursive]
ads-cli streams <path>
ads-cli export <path> <stream_name> -o <output>
```

Формат вывода: табличный, удобный для человека.

#### Режим 2: JSON-RPC сервер (для GUI)

```bash
ads-cli serve --stdio [--log-level debug]
```

- Читает JSON-RPC запросы из stdin (одна строка = один запрос)
- Отвечает в stdout (одна строка = один ответ)
- Логирование только в stderr
- Поддерживает несколько параллельных задач

## 5. JSON-RPC протокол

### 5.1. Формат сообщений

Все сообщения — одна JSON-строка + `\n`.

**Запрос:**
```json
{"jsonrpc":"2.0","id":1,"method":"<method>","params":{...}}
```

**Ответ (успех):**
```json
{"jsonrpc":"2.0","id":1,"result":{...}}
```

**Ответ (ошибка):**
```json
{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"...","data":{...}}}
```

**Уведомление (event от сервера):**
```json
{"jsonrpc":"2.0","method":"<event>","params":{...}}
```

### 5.2. Коды ошибок

| Код | Описание |
|---|---|
| `-32700` | Parse error (невалидный JSON) |
| `-32600` | Invalid request |
| `-32601` | Method not found |
| `-32602` | Invalid params |
| `-32603` | Internal error |
| `-32001` | Access denied |
| `-32002` | Not found |
| `-32003` | Not supported |
| `-32004` | Volume read-only |
| `-32005` | Cancelled |

### 5.3. Методы

#### `volumes.list`

Список примонтированных томов.

**Params:** — (пусто)

**Result:**
```json
{
  "volumes": [
    {
      "id": "C",
      "name": "C:",
      "mount_point": "C:\\",
      "file_system": "NTFS",
      "is_ntfs": true,
      "is_writable": true,
      "total_size": 500000000000,
      "free_size": 200000000000
    }
  ]
}
```

#### `scan.start`

Запуск сканирования.

**Params:**
```json
{
  "path": "C:\\data",
  "recursive": true,
  "include_hidden": true,
  "include_system": false,
  "include_directories": true,
  "follow_symlinks": false
}
```

**Result:**
```json
{
  "job_id": "scan-uuid-123"
}
```

**Events (отправляются по мере сканирования):**

```json
{"jsonrpc":"2.0","method":"scan.progress","params":{
  "job_id":"scan-uuid-123",
  "files_scanned":1000,
  "dirs_scanned":50,
  "streams_found":15,
  "errors":2,
  "current_path":"C:\\data\\subfolder"
}}
```

```json
{"jsonrpc":"2.0","method":"scan.streamFound","params":{
  "job_id":"scan-uuid-123",
  "stream":{
    "name":"Zone.Identifier",
    "size":128,
    "owner_path":"C:\\data\\file.txt"
  }
}}
```

```json
{"jsonrpc":"2.0","method":"scan.error","params":{
  "job_id":"scan-uuid-123",
  "path":"C:\\data\\protected",
  "error":"Access denied"
}}
```

```json
{"jsonrpc":"2.0","method":"scan.completed","params":{
  "job_id":"scan-uuid-123",
  "total_files":50000,
  "total_streams":150,
  "errors":12,
  "duration_ms":45230
}}
```

#### `scan.cancel`

Отмена сканирования.

**Params:**
```json
{
  "job_id": "scan-uuid-123"
}
```

**Result:**
```json
{
  "cancelled": true
}
```

#### `streams.list`

Потоки для конкретного файла/папки.

**Params:**
```json
{
  "path": "C:\\data\\file.txt"
}
```

**Result:**
```json
{
  "streams": [
    {
      "name": "Zone.Identifier",
      "size": 128,
      "owner_path": "C:\\data\\file.txt"
    },
    {
      "name": "MyStream",
      "size": 4096,
      "owner_path": "C:\\data\\file.txt"
    }
  ]
}
```

#### `stream.read`

Чтение части потока (для предпросмотра).

**Params:**
```json
{
  "path": "C:\\data\\file.txt",
  "stream_name": "Zone.Identifier",
  "offset": 0,
  "length": 4096
}
```

**Result:**
```json
{
  "data_base64": "W1pvbmVUcmFuc2Zlcl0NClpvbmVJZD0z",
  "offset": 0,
  "length": 29,
  "eof": true
}
```

**Важно:** `length` не должен превышать 1 MiB. Большие потоки читать по частям.

#### `stream.export`

Экспорт потока в файл (core сам пишет).

**Params:**
```json
{
  "path": "C:\\data\\file.txt",
  "stream_name": "Zone.Identifier",
  "destination": "C:\\Users\\user\\export.bin"
}
```

**Result:**
```json
{
  "bytes_written": 128,
  "sha256": "abc123..."
}
```

#### `stream.exportAll` (опционально)

Экспорт всех потоков файла в директорию.

**Params:**
```json
{
  "path": "C:\\data\\file.txt",
  "destination_dir": "C:\\Users\\user\\exported\\"
}
```

**Result:**
```json
{
  "streams_exported": 2,
  "files": [
    "C:\\Users\\user\\exported\\file.txt_Zone.Identifier.bin",
    "C:\\Users\\user\\exported\\file.txt_MyStream.bin"
  ]
}
```

## 6. GUI (Avalonia/C#)

### 6.1. Стек

- .NET 8.0
- Avalonia 11.x
- CommunityToolkit.Mvvm
- System.Text.Json

### 6.2. Структура

#### `Services/CoreProcessService.cs`

Отвечает за:
- Запуск `ads-cli serve --stdio`
- Управление жизненным циклом процесса
- Перезапуск при падении
- Graceful shutdown

#### `Services/JsonRpcService.cs`

Отвечает за:
- Сериализацию/десериализацию JSON-RPC
- Сопоставление запросов и ответов по `id`
- Обработку уведомлений (events)

#### `Services/CoreClient.cs`

Высокоуровневый API:
- `ListVolumesAsync()`
- `StartScanAsync(path, options)`
- `CancelScanAsync(jobId)`
- `ListStreamsAsync(path)`
- `ReadStreamAsync(path, streamName, offset, length)`
- `ExportStreamAsync(path, streamName, destination)`

#### `ViewModels/MainViewModel.cs`

- Список томов
- Текущий выбранный путь
- Команды для UI
- Подписка на события сканирования

#### `ViewModels/ScanViewModel.cs`

- Результаты сканирования
- Фильтры и поиск
- Прогресс

#### `ViewModels/StreamPreviewViewModel.cs`

- Предпросмотр содержимого потока
- Переключение режимов (hex/text)
- Постраничное чтение

### 6.3. Основной UI

```
+-------------------------------------------------------------+
| Volumes                                          [Refresh]  |
+----------------+--------------------------------------------+
| Tree           | Scan results                               |
|                |                                            |
| C:\            | File           Stream         Size  Actions|
| D:\            | file1.txt      Zone.Id        128B  [View] |
| /mnt/win       | file2.dll      metadata       4KB   [View] |
+----------------+--------------------------------------------+
| Preview (Zone.Identifier)                                    |
| [Text] [Hex]                                                 |
|                                                              |
| [ZoneTransfer]                                               |
| ZoneId=3                                                     |
+-------------------------------------------------------------+
| Progress: 1200 files, 35 streams, 2 errors      [Cancel]   |
+-------------------------------------------------------------+
```

### 6.4. Взаимодействие с core

**Важно:** GUI не должен читать большие потоки целиком.

- Для предпросмотра: запрашивать блоки по 64 KiB
- Для экспорта: вызывать `stream.export` (core сам пишет файл)
- Для сканирования: подписываться на события `scan.*`

## 7. План реализации

### Этап 1: Core библиотека с Windows backend (приоритет)

1. Базовые типы (`types.rs`, `error.rs`)
2. Trait `AdsBackend`
3. Реализация `WindowsBackend`:
   - `list_volumes` через `GetLogicalDriveStringsW` + `GetVolumeInformationW`
   - `list_streams` через `FindFirstStreamW` / `FindNextStreamW`
   - `read_stream` через `CreateFileW` с `path:stream:$DATA`
   - `export_stream` через копирование блоками
4. Юнит-тесты для Windows backend

### Этап 2: CLI

1. Парсинг аргументов через `clap`
2. Команды: `volumes`, `scan`, `streams`, `export`
3. Человекочитаемый вывод
4. JSON-RPC сервер в режиме `serve --stdio`
5. Обработка ошибок и логирование

### Этап 3: Avalonia GUI

1. Базовая структура проекта
2. `CoreProcessService` — запуск sidecar
3. `JsonRpcService` — клиент JSON-RPC
4. `CoreClient` — высокоуровневый API
5. `MainViewModel` — список томов и базовый UI
6. `ScanViewModel` — сканирование и прогресс
7. `StreamPreviewViewModel` — предпросмотр
8. Диалоги экспорта

### Этап 4: Linux backend

1. Определение NTFS через `statfs` / `/proc/self/mountinfo`
2. `list_streams` через `listxattr`
3. `read_stream` / `export_stream` через `getxattr`
4. Тесты на Linux

### Этап 5: Полировка и документация

1. CI/CD через GitHub Actions
2. Пользовательская документация
3. Примеры использования
4. Релизные сборки

## 8. Технические требования

### Общие

- Rust edition 2021
- .NET 8.0 LTS
- Все ошибки структурированные (не паники через FFI/IPC границы)
- Сканирование асинхронное с поддержкой отмены через `CancellationToken`
- Большие потоки читать блоками (не загружать целиком в память)
- Пути корректно обрабатываются на обеих платформах
- Windows: поддержка длинных путей через `\\?\`
- Linux: проверка что FS = NTFS перед использованием xattr

### Безопасность

- **v0.1:** только чтение и экспорт, без создания/удаления потоков
- Все модифицирующие операции (в будущих версиях) требуют подтверждения
- Перед удалением предлагать экспорт как backup
- Не показывать пароли/чувствительные данные в логах
- Не запускать GUI от администратора без необходимости

### Производительность

- Сканирование не должно блокировать UI
- Прогресс агрегировать (не отправлять событие на каждый файл)
- Использовать параллелизм с осторожностью (на HDD — последовательно)
- Кэшировать результаты для повторного просмотра

### Отказоустойчивость

- Падение core не должно ронять GUI
- GUI должен показывать понятную ошибку при падении sidecar
- Все операции должны иметь таймауты
- Поддержка retry для временных ошибок

## 9. Тестовые сценарии

### Файлы для тестирования

- Маленький текстовый файл с ADS
- Бинарный файл с ADS
- Большой файл (>100 MB) с ADS
- Файл с несколькими ADS
- Файл с кириллическим именем и ADS
- Файл с длинным путем и ADS
- Папка с ADS
- Скрытый/системный файл с ADS
- Файл без прав доступа

### Проверки

- Перечисление томов
- Перечисление потоков для файла
- Перечисление потоков для папки
- Чтение маленького потока (< 4 KiB)
- Чтение большого потока (> 1 MiB) блоками
- Экспорт потока в файл
- Экспорт всех потоков файла
- Сканирование директории
- Сканирование тома
- Отмена сканирования
- Обработка ошибок доступа
- Обработка read-only тома

## 10. Заметки

### Что НЕ делать в v0.1

- Работа с сырыми дисками (raw NTFS)
- Парсинг MFT
- Создание/удаление потоков
- Восстановление удаленных потоков
- Массовое удаление
- Редактирование содержимого потоков
- Автоматическое монтирование томов

### Что добавить в будущих версиях

- **v0.2:** создание и удаление потоков с backup
- **v0.3:** фильтрация, поиск, группировка, хеши
- **v0.4:** сохранение и загрузка отчетов
- **v1.0:** опциональный raw NTFS scanner (read-only)
- **v1.1:** интеграция с VirusTotal для подозрительных потоков
- **v2.0:** плагинная архитектура для анализа потоков

### Имена потоков

- Показывать имя без `:$DATA` в GUI
- При экспорте санитизировать имена (заменять недопустимые символы)
- Обрабатывать кириллические имена
- Обрабатывать специальные имена (`Zone.Identifier`, `favicon`, и т.д.)

### Известные проблемы

- Windows Fast Startup может делать NTFS тома read-only в Linux
- `ntfs-3g` и `ntfs3` могут по-разному работать с xattr
- Некоторые системные файлы могут быть заблокированы
- Длинные пути (> 260 символов) требуют специального обращения в Windows