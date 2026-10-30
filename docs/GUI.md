# Avalonia GUI Specification

## Overview

GUI-клиент на базе Avalonia (.NET 8), который запускает `ads-cli serve --stdio` как sidecar-процесс и общается с ним через JSON-RPC 2.0.

Полный протокол описан в `SPEC.md` раздел 5.

## Architecture

### Слой Services

- `CoreProcessService` — управление процессом ads-cli
- `JsonRpcService` — JSON-RPC клиент поверх stdin/stdout
- `CoreClient` — высокоуровневый API для ViewModel'ей

### Слой ViewModel (MVVM)

- `MainViewModel` — главное окно
- `ScanViewModel` — управление сканированием
- `StreamPreviewViewModel` — предпросмотр потоков
- `ExportViewModel` — диалог экспорта

### Слой Views

- `MainWindow.axaml` — главное окно
- `StreamPreviewWindow.axaml` — окно предпросмотра
- `ExportDialog.axaml` — диалог сохранения файла

## UI Structure

### MainWindow

```
+-------------------------------------------------------------+
| NtfsStreamsViewer                            [Refresh] [?]  |
+----------------+--------------------------------------------+
| Volumes        | Scan results                               |
|                |                                            |
| [C: NTFS]      | File           Stream         Size  Actions|
| [D: NTFS]      | file1.txt      Zone.Id        128B  [View] |
| [/mnt/win]     | file2.dll      metadata       4KB   [View] |
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

## CoreProcessService

### Responsibilities

- Найти и запустить бинарник `ads-cli`
- Управлять жизненным циклом процесса
- Предоставить потоки stdin/stdout
- Обрабатывать падение процесса

### Interface

```csharp
public interface ICoreProcessService : IAsyncDisposable
{
    Task<Stream> GetStdinAsync();
    Task<Stream> GetStdoutAsync();
    event EventHandler<ProcessExitedEventArgs> ProcessExited;
    Task RestartAsync();
}
```

### Process Discovery

Порядок поиска бинарника:

1. Рядом с исполняемым файлом GUI
2. `../cli/target/release/` (для разработки)
3. Переменная окружения PATH

Имя бинарника зависит от платформы:

- Windows: `ads-cli.exe`
- Linux: `ads-cli`

## JsonRpcService

### Responsibilities

- Отправлять JSON-RPC запросы и получать ответы
- Обрабатывать уведомления (события без `id`)
- Сопоставлять запросы и ответы по `id`
- Сериализовать/десериализовать JSON

### Interface

```csharp
public interface IJsonRpcService
{
    Task<T> SendRequestAsync<T>(string method, object parameters);
    event EventHandler<JsonRpcNotificationEventArgs> NotificationReceived;
}
```

### Notification Events

- `scan.progress` — обновить прогресс-бар
- `scan.streamFound` — добавить поток в результаты
- `scan.error` — залогировать ошибку
- `scan.completed` — завершить сканирование

## CoreClient

### Interface

```csharp
public interface ICoreClient
{
    Task<List<VolumeInfo>> ListVolumesAsync();
    Task<string> StartScanAsync(string path, ScanOptions options);
    Task CancelScanAsync(string jobId);
    Task<List<StreamInfo>> ListStreamsAsync(string path);
    Task<ReadResult> ReadStreamAsync(string path, string streamName,
                                      ulong offset, int length);
    Task<ExportResult> ExportStreamAsync(string path, string streamName,
                                         string destinationPath);
}
```

## ViewModels

### MainViewModel

#### Properties

- `ObservableCollection<VolumeInfo> Volumes`
- `VolumeInfo? SelectedVolume`
- `ObservableCollection<ScanStreamItem> ScanResults`
- `bool IsScanning`
- `double ScanProgress`
- `string CurrentScanPath`
- `int FilesScanned`
- `int StreamsFound`
- `int ErrorsCount`

#### Commands

- `RefreshVolumesCommand` — перезагрузить список томов
- `StartScanCommand` — начать сканирование
- `CancelScanCommand` — отменить текущее сканирование
- `ViewStreamCommand` — открыть предпросмотр
- `ExportStreamCommand` — экспортировать в файл

#### Event Handlers

Подписка на уведомления JSON-RPC:

- `scan.progress` → обновить свойства прогресса
- `scan.streamFound` → добавить в `ScanResults`
- `scan.completed` → установить `IsScanning = false`

### StreamPreviewViewModel

#### Properties

- `string StreamName`
- `string Content`
- `bool IsHexMode`
- `bool HasMoreData`
- `ulong CurrentOffset`

#### Commands

- `ToggleHexModeCommand`
- `LoadMoreCommand` — прочитать следующий блок
- `ExportCommand` — экспортировать поток

#### Initialization

При открытии окна:

1. Вызвать `ReadStreamAsync` с `offset=0`, `length=4096`
2. Декодировать base64 и отобразить
3. Установить `HasMoreData` на основе флага `eof`

## Cross-cutting Concerns

### Error Handling

- Падение процесса → показать диалог, предложить перезапуск
- Ошибка сети → показать уведомление
- Невалидный JSON → залогировать и продолжить

### Thread Safety

- Все обновления UI через `Dispatcher.UIThread.InvokeAsync`
- Использовать `ObservableCollection` для автоматического обновления
- `JsonRpcService` работает в фоновом потоке

### Localization

- Использовать resource-файлы для строк интерфейса
- Язык по умолчанию: английский
- В будущем: русский, немецкий

### Theming

- Поддержка светлой и тёмной темы
- Использовать встроенную систему темизации Avalonia
- По умолчанию использовать тему ОС

## Testing Strategy

### Unit Tests

- Логика ViewModel (команды, изменения свойств)
- Сериализация/десериализация JSON
- Обработка ошибок

### Integration Tests

- Mock `CoreProcessService`
- Тестирование потока JSON-RPC
- Проверка обработки уведомлений

### Manual Testing

- Реальный процесс `ads-cli`
- Кроссплатформенность (Windows/Linux)
- Большие тома (производительность)

## Build and Deployment

### Windows

- Публикация в один файл с self-contained рантаймом
- Бинарник `ads-cli.exe` в той же директории
- Опционально: установщик

### Linux

- Публикация как self-contained
- Бинарник `ads-cli` рядом с приложением
- Опционально: пакеты `.deb`/`.rpm`
- AppImage для дистрибуции

### CI/CD

- Сборка при пуше в `main`
- Запуск юнит-тестов
- Создание артефактов для Windows и Linux
- Публикация в GitHub Releases