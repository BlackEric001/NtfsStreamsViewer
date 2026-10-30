# Техническое задание: Этап 3 — Реализация Avalonia GUI (C#)

## Контекст
Бэкенд на Rust (core) и CLI-сервер (ads-cli) полностью реализованы и покрыты тестами. 
Бинарник `ads-cli` поддерживает команду `ads-cli serve --stdio`, которая поднимает JSON-RPC 2.0 сервер на stdin/stdout.
Твоя задача — создать кроссплатформенный GUI на базе Avalonia (.NET 8), который будет запускать `ads-cli` как sidecar-процесс и общаться с ним по протоколу JSON-RPC.

## Задачи

### 1. Инициализация проекта
- В папке `/gui` создай Avalonia проект (MVVM Application, .NET 8).
- Добавь NuGet пакеты: `Avalonia`, `Avalonia.Desktop`, `CommunityToolkit.Mvvm`, `System.Text.Json`.

### 2. Инфраструктура (Services)
Создай папку `Services` и реализуй следующие классы:

#### `CoreProcessService.cs`
- Отвечает за запуск бинарника `ads-cli serve --stdio`.
- Путь к `ads-cli` (или `ads-cli.exe` на Windows) должен искаться рядом с исполняемым файлом GUI.
- Управляет потоками `StandardInput` и `StandardOutput`.
- Реализует `IAsyncDisposable` для корректного завершения процесса (`Graceful shutdown`).

#### `JsonRpcService.cs`
- Читает stdout процесса построчно (каждая строка — JSON).
- Отправляет запросы в stdin.
- Сопоставляет ответы с запросами по `id` (используй `TaskCompletionSource`).
- Обрабатывает уведомления (events) без `id` (например, `scan.progress`, `scan.streamFound`) и пробрасывает их через C# события (events) или `IObservable`.

#### `CoreClient.cs`
Высокоуровневый API для ViewModel'ей. Должен содержать методы:
- `Task<List<VolumeInfo>> ListVolumesAsync()`
- `Task<string> StartScanAsync(string path, ScanOptions options)` (возвращает job_id)
- `Task CancelScanAsync(string jobId)`
- `Task<List<StreamInfo>> ListStreamsAsync(string path)`
- `Task<ReadResult> ReadStreamAsync(string path, string streamName, ulong offset, int length)`
- `Task<ExportResult> ExportStreamAsync(string path, string streamName, string destinationPath)`

### 3. Модели (DTO)
Создай C# классы (с атрибутами `JsonPropertyName` для System.Text.Json), строго соответствующие Rust-типами из `core/src/types.rs`:
- `VolumeInfo`, `StreamInfo`, `ScanOptions`, `ScanProgress`, `ReadResult` (с полем `data_base64`), `ExportResult`.
- `JsonRpcRequest`, `JsonRpcResponse<T>`, `JsonRpcError`.

### 4. UI и ViewModel (MVVM)

#### MainWindow
- **Левая панель:** Список доступных NTFS томов (вызов `ListVolumesAsync` при старте). Показывать `Name`, `MountPoint`, `IsWritable`.
- **Центральная панель:** DataGrid со списком найденных потоков.
- **Кнопка "Scan":** Запускает сканирование выбранного тома/папки. Во время сканирования показывает ProgressBar.
- **Кнопка "Cancel Scan":** Отменяет текущее сканирование.
- **Обработка событий:** Подпишись на события `scan.streamFound` от JSON-RPC и динамически добавляй их в DataGrid. Подпишись на `scan.progress` для обновления ProgressBar.

#### StreamPreviewWindow (или панель справа)
- При двойном клике на поток в DataGrid открывать предпросмотр.
- Вызов `ReadStreamAsync` с `offset=0`, `length=4096`.
- Декодировать `data_base64` и показывать текст.
- Если `eof == false`, добавить кнопку "Load More" (читать следующие 4096 байт).
- Кнопка "Export..." — вызывает системный диалог сохранения файла (SaveFileDialog) и вызывает `ExportStreamAsync`.

### 5. Важные нюансы
- **Асинхронность:** Все вызовы к Core должны быть асинхронными (`async/await`), чтобы не вешать UI.
- **Потокобезопасность:** Обновление UI (ObservableCollection) должно происходить строго в UI-потоке (используй `Dispatcher.UIThread.InvokeAsync` или `Avalonia.Threading`).
- **Обработка ошибок:** Если процесс `ads-cli` падает, GUI должен показать понятное сообщение и предложить перезапустить sidecar.
- **Кодировка:** JSON-RPC работает в UTF-8. Убедись, что stdin/stdout настроены на UTF-8.

Приступай к реализации. Начинай с создания проекта Avalonia и настройки `CoreProcessService`.