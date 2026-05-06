# FileOrganizer

A .NET 8 Windows Service that watches one or more source folders for files matching configurable patterns and automatically **moves**, **copies**, or **deletes** them based on per-rule settings. All activity and log output are rendered as self-refreshing HTML files you can open in any browser.

---

## Table of Contents

1. [Features](#features)
2. [Project Structure](#project-structure)
3. [Prerequisites](#prerequisites)
4. [Configuration Reference](#configuration-reference)
   - [Top-level settings](#top-level-settings)
   - [FileRule settings](#filerule-settings)
   - [File age fields](#file-age-fields)
   - [Full example (appsettings.json)](#full-example-appsettingsjson)
5. [Running Locally (Console Mode)](#running-locally-console-mode)
6. [Debugging in VS Code](#debugging-in-vs-code)
7. [Installing as a Windows Service](#installing-as-a-windows-service)
8. [Uninstalling the Windows Service](#uninstalling-the-windows-service)
9. [HTML Reports](#html-reports)
   - [Activity Report](#activity-report)
   - [Log Viewer](#log-viewer)
   - [Persistence across restarts](#persistence-across-restarts)
10. [Live Dashboard](#live-dashboard)
11. [Logging](#logging)
12. [Hot-Reload Configuration](#hot-reload-configuration)

---

## Features

- **Multiple file rules** — define any number of rules, each with its own patterns, source folders, target folder, and operation.
- **Move, Copy, or Delete** — configurable per rule.
- **Multiple file patterns per rule** — e.g. `["*.dmp", "*.log"]`; patterns are union-matched and deduplicated.
- **Sub-directory scanning** — optionally recurse into sub-folders.
- **Duplicate handling** — Skip / Overwrite / Rename (timestamp-suffixed) per rule.
- **File age guards** — two independent per-rule age filters:
  - `MostRecentAgeDays` — only process files modified/created within the last N days.
  - `OlderThanDays` — only process files whose most-recent change is older than N days (useful for Delete rules).
- **Prepend source folder name** — optionally prefix the destination file name with the last segment of the source folder path (e.g. `Partners-filename.dmp`).
- **Activity Report** — self-refreshing HTML showing all operations within the retention window, colour-coded by status. Columns are drag-resizable. Persisted across restarts.
- **Log Viewer** — self-refreshing HTML showing all log messages within the retention window, with per-level filter buttons. Persisted across restarts.
- **Live Dashboard** — lightweight HTTP dashboard on `localhost:{DashboardPort}` showing service status, recent errors, and per-rule file listings.
- **Hot-reload config** — edit `appsettings.json` while the service is running; all changes take effect on the next polling cycle.
- **Windows Event Log** integration — log messages also written to the Windows Application event log.

---

## Project Structure

```
FileOrganizer/
├── .gitignore
├── FileOrganizer.csproj
├── Program.cs                          Entry point; builds and runs the host
├── Worker.cs                           BackgroundService polling loop
├── appsettings.json                    Main configuration (hot-reload enabled)
├── appsettings.Development.json        Local overrides (console logging, Debug level)
├── install-service.bat                 Publishes + registers + starts the Windows Service
├── uninstall-service.bat               Stops + removes the Windows Service
├── .vscode/
│   ├── launch.json                     F5 debug configuration
│   └── tasks.json                      Pre-launch build task
├── Models/
│   ├── FileOrganizerConfig.cs          Top-level config shape
│   ├── FileRule.cs                     Rule shape + FileOperation / DuplicateHandling enums
│   └── FileActivityRecord.cs          Activity record shape + ActivityStatus enum
├── Services/
│   ├── FileProcessorService.cs         Core scan / move / copy / delete logic
│   ├── ActivityReportService.cs        Activity record store + HTML report writer
│   └── DashboardServer.cs             Lightweight HTTP dashboard (HttpListener)
└── Logging/
    ├── HtmlLogEntry.cs                 Log entry shape
    ├── HtmlLogBuffer.cs                Log entry store + HTML log viewer writer
    ├── HtmlLogger.cs                   ILogger implementation
    └── HtmlLoggerProvider.cs           ILoggerProvider — one logger per category
```

---

## Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 8.0 or later |
| Operating System | Windows (required for Windows Service host) |

Install the .NET SDK from https://dotnet.microsoft.com/download.

---

## Configuration Reference

All settings live under the `"FileOrganizer"` section in `appsettings.json`.

### Top-level settings

| Key | Type | Default | Description |
|---|---|---|---|
| `PollingIntervalSeconds` | `int` | `60` | How often (seconds) the service scans all source folders. Minimum enforced: 5 s. |
| `ReportFilePath` | `string` | `"FileOrganizer_Report.html"` | Full path of the HTML **activity report** file. Leave empty to disable. |
| `LogFilePath` | `string` | `"FileOrganizer_Log.html"` | Full path of the HTML **log viewer** file. Leave empty to disable. |
| `LogMaxEntries` | `int` | `200` | Maximum number of log entries kept in memory and the HTML log viewer. |
| `LogRetentionDays` | `int` | `7` | Drop log entries older than this many days. `0` = keep forever. |
| `ReportRetentionDays` | `int` | `7` | Drop activity report records older than this many days. `0` = keep forever. |
| `HtmlRefreshSeconds` | `int` | `30` | Browser auto-refresh interval for both HTML files (seconds). |
| `DashboardPort` | `int` | `5051` | Port the live Dashboard listens on (`localhost` only). Requires a service restart to change. |

### FileRule settings

Each object in the `"Rules"` array supports:

| Key | Type | Default | Description |
|---|---|---|---|
| `Name` | `string` | `""` | Friendly name shown in logs and reports. |
| `Enabled` | `bool` | `true` | Set to `false` to skip this rule without removing it. |
| `FilePatterns` | `string[]` | `[]` | One or more glob patterns, e.g. `["*.dmp", "*.log"]`. Files matching any pattern are processed. |
| `SourceFolders` | `string[]` | `[]` | Folders to scan. UNC paths (`\\server\share\...`) are supported. |
| `TargetFolder` | `string` | `""` | Destination folder (not required for `Delete`). Created automatically if absent. |
| `Operation` | `"Copy"`, `"Move"`, or `"Delete"` | `"Copy"` | Action to perform on matched files. |
| `IncludeSubDirectories` | `bool` | `false` | When `true`, sub-directories inside each `SourceFolder` are also scanned. |
| `DuplicateHandling` | `"Skip"`, `"Overwrite"`, or `"Rename"` | `"Rename"` | What to do when a file with the same name already exists in the target. `Rename` appends a timestamp. Not applicable for `Delete`. |
| `MostRecentAgeDays` | `double` | `0` | Only process files whose most-recent timestamp is **within** this many days. `0` = disabled. Fractional values supported (e.g. `0.5` = 12 h). Negative = error logged, file skipped. |
| `OlderThanDays` | `double` | `0` | Only process files whose most-recent timestamp is **older than** this many days. `0` = disabled. Fractional values supported. Negative = error logged, file skipped. |
| `PrependSourceFolderName` | `bool` | `false` | When `true`, the last path segment of the source folder is prepended to the destination file name with a `-` separator (e.g. `Partners-file.dmp`). |

### File age fields

The two age fields are independent and can be combined:

```
Files on disk (sorted by age, newest → oldest)
─────────────────────────────────────────────────────────────────────
  0 days ← NOW                                               7 days →

  │◄── MostRecentAgeDays: 1 ──►│
  │         PROCESS             │         (too old, skipped)         │

                                │◄─── OlderThanDays: 1 ────────────►│
                  (too new, skipped)     PROCESS (e.g. Delete)        │
```

- **`MostRecentAgeDays`** — use on Copy/Move rules to only pick up recently changed files.
- **`OlderThanDays`** — use on Delete rules to only clean up files that haven't been touched in a while.
- Both use `max(LastWriteTime, CreationTime)` so newly created but unmodified files are handled correctly.
- Set to `0` to disable the guard entirely.

### Full example (appsettings.json)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    },
    "EventLog": {
      "SourceName": "FileOrganizer",
      "LogName": "Application",
      "LogLevel": { "Default": "Information" }
    }
  },

  "FileOrganizer": {
    "PollingIntervalSeconds": 60,
    "ReportFilePath": "C:\\Organized\\Report\\FileOrganizerReport.html",
    "LogFilePath":    "C:\\Organized\\Report\\FileOrganizerLog.html",
    "LogMaxEntries": 200,
    "LogRetentionDays": 7,
    "ReportRetentionDays": 7,
    "HtmlRefreshSeconds": 30,
    "DashboardPort": 5051,

    "Rules": [
      {
        "Name": "DumpFiles",
        "Enabled": true,
        "FilePatterns": [ "*.dmp" ],
        "SourceFolders": [
          "\\\\fileserver\\share\\Dumps\\AGI",
          "\\\\fileserver\\share\\Dumps\\ISG",
          "\\\\fileserver\\share\\Dumps\\Partners"
        ],
        "TargetFolder": "C:\\Organized\\DumpFiles",
        "Operation": "Copy",
        "IncludeSubDirectories": false,
        "DuplicateHandling": "Overwrite",
        "MostRecentAgeDays": 1,
        "OlderThanDays": 0,
        "PrependSourceFolderName": true
      },
      {
        "Name": "ParallelStack",
        "Enabled": true,
        "FilePatterns": [ "*.png" ],
        "SourceFolders": [ "C:\\Organized\\DumpFiles" ],
        "TargetFolder": "\\\\fileserver\\share\\ParallelStacks",
        "Operation": "Copy",
        "IncludeSubDirectories": true,
        "DuplicateHandling": "Overwrite",
        "MostRecentAgeDays": 0,
        "OlderThanDays": 0,
        "PrependSourceFolderName": false
      },
      {
        "Name": "CleanupOldFiles",
        "Enabled": false,
        "FilePatterns": [ "*.dmp", "*.png" ],
        "SourceFolders": [ "C:\\Organized\\DumpFiles" ],
        "TargetFolder": "",
        "Operation": "Delete",
        "IncludeSubDirectories": false,
        "DuplicateHandling": "Skip",
        "MostRecentAgeDays": 0,
        "OlderThanDays": 7,
        "PrependSourceFolderName": false
      }
    ]
  }
}
```

---

## Running Locally (Console Mode)

```powershell
cd C:\as7.workspace\Repositories\FileOrganizer

# Debug build (verbose logging via appsettings.Development.json)
dotnet run

# Release build
dotnet run --configuration Release
```

Press `Ctrl+C` to stop.

> `DOTNET_ENVIRONMENT` defaults to `Development` when running via `dotnet run`, which activates `appsettings.Development.json` and enables `Debug`-level console logging.

---

## Debugging in VS Code

A `launch.json` and `tasks.json` are included in `.vscode/`.

| Config | How |
|---|---|
| **Debug FileOrganizer** | Press **F5** — builds a Debug binary and launches with the debugger attached. `DOTNET_ENVIRONMENT=Development` is set automatically. |
| **Attach to FileOrganizer** | Start the service in a terminal (`dotnet run`), then press **F5** and pick "Attach to FileOrganizer". |

Useful breakpoints:
- `FileProcessorService.cs` → `ProcessFileAsync()` — see why each file is skipped or processed.
- `ActivityReportService.cs` → `AddRecord()` — confirm a file operation was recorded.

To get verbose log output without a debugger, set in `appsettings.json`:
```json
"LogLevel": { "Default": "Debug" }
```

---

## Installing as a Windows Service

Run **as Administrator** from the repository root:

```bat
install-service.bat
```

What the script does:

1. Publishes a self-contained `win-x64` binary to the `publish\` subfolder.
2. Registers the service with `sc create` (auto-start).
3. Sets a failure recovery policy (restart after 5 s → 10 s → 30 s).
4. Starts the service immediately.

The active configuration file after installation is:
```
<repo>\publish\appsettings.json
```

Edit that file to change rules or paths — hot-reload applies, no restart needed.

**Verify / control the service:**

```powershell
Get-Service   FileOrganizer
Start-Service FileOrganizer
Stop-Service  FileOrganizer
```

---

## Uninstalling the Windows Service

Run **as Administrator**:

```bat
uninstall-service.bat
```

Stops and removes the service registration. The `publish\` folder and config files are left on disk.

---

## HTML Reports

Both files are written to the paths set in `appsettings.json`. Open in any browser — they auto-refresh every `HtmlRefreshSeconds` seconds.

### Activity Report

**Key:** `ReportFilePath`

Shows all file operations within the last `ReportRetentionDays` days, newest first.

| Column | Description |
|---|---|
| # | Row number |
| Time | Timestamp of the operation |
| File Name | Destination file name |
| Source Folder | Folder the file was taken from |
| Target Folder | Destination folder (empty for Delete) |
| Operation | `Copy` / `Move` / `Delete` pill |
| Status | `In Progress` / `Success` / `Failed` — hover Failed pill for the error message |

Row colours: green = Success, red = Failed, amber = In Progress.  
Columns are **auto-sized to content** and can be **drag-resized** by grabbing the right edge of any header.

### Log Viewer

**Key:** `LogFilePath`

Shows all service log messages within the last `LogRetentionDays` days, capped at `LogMaxEntries`, newest first.

| Column | Description |
|---|---|
| Timestamp | `yyyy-MM-dd HH:mm:ss.fff` |
| Level | TRACE / DEBUG / INFO / WARN / ERROR / CRIT |
| Source | Shortened class name (hover for full namespace) |
| Message | Log message; exceptions have a collapsible block with the full stack trace |

**Filter buttons** at the top toggle individual log levels. Clicking **ALL** resets to show everything.

### Persistence across restarts

Both the activity report and log viewer survive service restarts. On shutdown, records are persisted to JSON sidecar files next to the HTML outputs:

| Sidecar file | Contents |
|---|---|
| `FileOrganizerReport.records.json` | Activity report records |
| `FileOrganizerLog.entries.json` | Log viewer entries |

On startup these are loaded back automatically. Old entries outside the retention window are evicted on load.

---

## Live Dashboard

Open `http://localhost:{DashboardPort}/` (default: http://localhost:5051/) in a browser while the service is running.

- Service status pill (Running / Error)
- Recent error count (last 5 minutes)
- Per-rule file tables with sortable columns and live filter input
- Auto-polls every 5 seconds
- JSON API at `http://localhost:5051/api/status`

---

## Logging

Log messages are written to three sinks simultaneously:

| Sink | When active | Notes |
|---|---|---|
| Console | `dotnet run` (Development) | Coloured output; configured in `appsettings.Development.json` |
| Windows Event Log | When running as a Windows Service | Application log, Source: `FileOrganizer` — view in **Event Viewer** |
| HTML Log Viewer | Always (when `LogFilePath` is set) | Browser-friendly; auto-refreshes every `HtmlRefreshSeconds` s |

Default log level: **Information**.  
Set `"Default": "Debug"` under `Logging:LogLevel` for verbose output.

---

## Hot-Reload Configuration

`appsettings.json` is loaded with `reloadOnChange: true`. The following take effect on the **next polling cycle** without restarting:

- Adding, removing, or toggling rules (`Enabled`)
- `PollingIntervalSeconds`
- Any rule field: `FilePatterns`, `SourceFolders`, `TargetFolder`, `Operation`, `DuplicateHandling`, `IncludeSubDirectories`, `MostRecentAgeDays`, `OlderThanDays`, `PrependSourceFolderName`
- `ReportFilePath`, `LogFilePath`, `LogMaxEntries`, `LogRetentionDays`, `ReportRetentionDays`, `HtmlRefreshSeconds`

> **Note:** `DashboardPort` requires a service restart to take effect.  
> When running as a Windows Service the active config is `publish\appsettings.json`, not the source repo copy.


---

## Table of Contents

1. [Features](#features)
2. [Project Structure](#project-structure)
3. [Prerequisites](#prerequisites)
4. [Configuration Reference](#configuration-reference)
   - [Top-level settings](#top-level-settings)
   - [FileRule settings](#filerule-settings)
   - [File age window](#file-age-window)
   - [Full example (appsettings.json)](#full-example-appsettingsjson)
5. [Running Locally (Console Mode)](#running-locally-console-mode)
6. [Installing as a Windows Service](#installing-as-a-windows-service)
7. [Uninstalling the Windows Service](#uninstalling-the-windows-service)
8. [HTML Reports](#html-reports)
   - [Activity Report](#activity-report)
   - [Log Viewer](#log-viewer)
9. [Logging](#logging)
10. [Hot-Reload Configuration](#hot-reload-configuration)

---

## Features

- **Multiple file rules** — define any number of rules, each with its own file pattern, source folders, target folder, and operation.
- **Move or Copy** — configurable per rule.
- **Sub-directory scanning** — optionally recurse into sub-folders.
- **Duplicate handling** — Skip / Overwrite / Rename (timestamp-suffixed) per rule.
- **File age window** — two complementary guards per rule:
  - `MinFileAgeMinutes` — skip files that may still be open/writing.
  - `MaxFileAgeMinutes` — only process recently created or modified files.
- **Activity Report** — a self-refreshing HTML file showing the last 25 file operations with colour-coded status (Success / Failed / In Progress). Columns auto-size to content and are drag-resizable.
- **Log Viewer** — a self-refreshing HTML file showing all service log messages from the last configurable number of hours, with per-level filter buttons.
- **Hot-reload config** — edit `appsettings.json` while the service is running; all rule and path changes take effect on the next polling cycle with no restart required.
- **Windows Event Log** integration — log messages also written to the Windows Application event log (source: `FileOrganizer`).

---

## Project Structure

```
FileOrganizer/
├── FileOrganizer.csproj
├── Program.cs                          Entry point; builds and runs the host
├── Worker.cs                           BackgroundService polling loop
├── appsettings.json                    Main configuration (hot-reload enabled)
├── appsettings.Development.json        Local overrides (console logging, Debug level)
├── install-service.bat                 Publishes + registers + starts the Windows Service
├── uninstall-service.bat               Stops + removes the Windows Service
├── Models/
│   ├── FileOrganizerConfig.cs          Top-level config shape
│   ├── FileRule.cs                     Rule shape + FileOperation / DuplicateHandling enums
│   └── FileActivityRecord.cs          Activity record shape + ActivityStatus enum
├── Services/
│   ├── FileProcessorService.cs         Core scan / move / copy logic
│   └── ActivityReportService.cs        Thread-safe ring-buffer + HTML activity report writer
└── Logging/
    ├── HtmlLogEntry.cs                 Log entry shape
    ├── HtmlLogBuffer.cs                Thread-safe ring-buffer + HTML log viewer writer
    ├── HtmlLogger.cs                   ILogger implementation
    └── HtmlLoggerProvider.cs           ILoggerProvider — one logger per category
```

---

## Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 8.0 or later |
| Operating System | Windows (required for Windows Service host) |

Install the .NET SDK from https://dotnet.microsoft.com/download.

---

## Configuration Reference

All settings live under the `"FileOrganizer"` section in `appsettings.json`.

### Top-level settings

| Key | Type | Default | Description |
|---|---|---|---|
| `PollingIntervalSeconds` | `int` | `60` | How often (in seconds) the service scans all source folders. Minimum enforced: 5 s. |
| `ReportFilePath` | `string` | `"FileOrganizer_Report.html"` | Full path of the HTML **activity report** file. Leave empty to disable. |
| `LogFilePath` | `string` | `"FileOrganizer_Log.html"` | Full path of the HTML **log viewer** file. Leave empty to disable. |
| `LogMaxEntries` | `int` | `200` | Maximum number of log entries kept in the HTML log viewer. Oldest entries are evicted first when this limit is reached. |
| `LogRetentionHours` | `int` | `24` | Log entries older than this many hours are dropped from the HTML log viewer on every write. Set to `0` to disable time-based eviction. |

### FileRule settings

Each object in the `"Rules"` array supports:

| Key | Type | Default | Description |
|---|---|---|---|
| `Name` | `string` | `""` | Friendly name shown in logs and the activity report. |
| `Enabled` | `bool` | `true` | Set to `false` to skip this rule without deleting it. |
| `FilePattern` | `string` | `"*.*"` | Glob pattern matched against file names, e.g. `"*.dmp"`, `"report_*.csv"`. |
| `SourceFolders` | `string[]` | `[]` | List of folders to scan. UNC paths (`\\server\share\...`) are supported. |
| `TargetFolder` | `string` | `""` | Destination folder. Created automatically if it does not exist. UNC paths supported. |
| `Operation` | `"Move"` or `"Copy"` | `"Copy"` | Whether to move or copy matched files. |
| `IncludeSubDirectories` | `bool` | `false` | When `true`, sub-directories inside each `SourceFolder` are also scanned. |
| `DuplicateHandling` | `"Skip"`, `"Overwrite"`, or `"Rename"` | `"Rename"` | What to do when a file with the same name already exists in the target folder. `Rename` appends a timestamp to the file name. |
| `MinFileAgeMinutes` | `int` | `0` | Skip files whose last-write time is **less than** N minutes ago. Guards against picking up files still being written. `0` = disabled. |
| `MaxFileAgeMinutes` | `int` | `0` | Skip files whose most-recent timestamp (creation or last-write, whichever is newer) is **older than** N minutes. Use this to limit processing to recently changed files only. `0` = disabled. |

### File age window

The two age settings work together to define a processing window. Example with `MinFileAgeMinutes: 3` and `MaxFileAgeMinutes: 5`:

```
NOW <--------------------------------------------------------------
     0 min   1 min   2 min   3 min   4 min   5 min   6 min
     [too fresh -- may still    [ PROCESS ]    [too old --
      be open/writing]                          ignored]
```

For `MaxFileAgeMinutes`, the service takes whichever is more recent of `LastWriteTime` and `CreationTime`, so a newly created but unmodified file is still caught correctly.

Set either value to `0` to disable that bound.

### Full example (appsettings.json)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    },
    "EventLog": {
      "SourceName": "FileOrganizer",
      "LogName": "Application",
      "LogLevel": {
        "Default": "Information"
      }
    }
  },

  "FileOrganizer": {
    "PollingIntervalSeconds": 60,
    "ReportFilePath": "C:\\Repositories\\FileOrganizer\\FileOrganizerReport.html",
    "LogFilePath": "C:\\Repositories\\FileOrganizer\\FileOrganizerLog.html",
    "LogMaxEntries": 200,
    "LogRetentionHours": 24,

    "Rules": [
      {
        "Name": "DumpFiles",
        "Enabled": true,
        "FilePattern": "*.dmp",
        "SourceFolders": [
          "\\\\fileserver\\share\\Dumps\\AGI",
          "\\\\fileserver\\share\\Dumps\\APG",
          "\\\\fileserver\\share\\Dumps\\ISG",
          "\\\\fileserver\\share\\Dumps\\Partners"
        ],
        "TargetFolder": "C:\\Organized\\DumpFiles",
        "Operation": "Copy",
        "IncludeSubDirectories": false,
        "DuplicateHandling": "Overwrite",
        "MinFileAgeMinutes": 3,
        "MaxFileAgeMinutes": 5
      },
      {
        "Name": "ParallelStack",
        "Enabled": false,
        "FilePattern": "*.png",
        "SourceFolders": [
          "C:\\Organized\\DumpFiles"
        ],
        "TargetFolder": "\\\\fileserver\\share\\ParallelStacks",
        "Operation": "Move",
        "IncludeSubDirectories": true,
        "DuplicateHandling": "Overwrite",
        "MinFileAgeMinutes": 3,
        "MaxFileAgeMinutes": 0
      }
    ]
  }
}
```

---

## Running Locally (Console Mode)

Use this during development — the service runs in the terminal window and writes coloured console output.

```powershell
cd C:\as7.workspace\Repositories\FileOrganizer

# Debug build (verbose logging via appsettings.Development.json)
dotnet run

# Release build
dotnet run --configuration Release
```

Press `Ctrl+C` to stop.

> The `DOTNET_ENVIRONMENT` variable defaults to `Development` when running via `dotnet run`, which activates `appsettings.Development.json` and enables `Debug`-level console logging.

---

## Installing as a Windows Service

Run **as Administrator** from the repository root:

```bat
install-service.bat
```

What the script does:

1. Publishes a self-contained `win-x64` binary to the `publish\` subfolder.
2. Registers the service with `sc create` (auto-start).
3. Sets a failure recovery policy (restart after 5 s → 10 s → 30 s).
4. Starts the service immediately.

After installation the active configuration file is:

```
<repo>\publish\appsettings.json
```

Edit that file to change rules or paths — no restart required (hot-reload).

**Verify the service is running:**

```powershell
Get-Service FileOrganizer
```

**Start / stop manually:**

```powershell
Start-Service FileOrganizer
Stop-Service  FileOrganizer
```

---

## Uninstalling the Windows Service

Run **as Administrator**:

```bat
uninstall-service.bat
```

This stops and removes the service registration. The `publish\` folder and `appsettings.json` are left on disk.

---

## HTML Reports

Both HTML files are written to the paths configured in `appsettings.json`. Open them in any browser — they auto-refresh automatically.

### Activity Report

**Key:** `ReportFilePath`
**Auto-refresh:** every 30 seconds

Displays the **last 25 file operations** in descending time order.

| Column | Description |
|---|---|
| Time | Timestamp of the operation |
| File Name | Name of the processed file (hover for full path) |
| Source Folder | Folder the file was taken from |
| Target Folder | Destination folder |
| Op | `Move` or `Copy` |
| Status | `In Progress` / `Success` / `Failed` (hover a Failed row's pill to see the error message) |

Row colours: green = Success, red = Failed, amber = In Progress.

Columns are **auto-sized to content** on load and can be **drag-resized** by grabbing the right edge of any column header.

### Log Viewer

**Key:** `LogFilePath`
**Auto-refresh:** every 10 seconds

Displays all service log messages from the last `LogRetentionHours` hours (default: 24), capped to `LogMaxEntries` entries (default: 200), newest first.

| Column | Description |
|---|---|
| Timestamp | `yyyy-MM-dd HH:mm:ss.fff` |
| Level | TRACE / DEBUG / INFO / WARN / ERROR / CRIT |
| Source | Shortened class name (hover for full namespace) |
| Message | Log message; failed operations include a collapsible exception block with full stack trace |

**Filter buttons** at the top let you toggle individual log levels. Clicking ALL resets to show everything.

---

## Logging

Log messages are written to three sinks simultaneously:

| Sink | When active | Notes |
|---|---|---|
| Console | `dotnet run` (Development) | Coloured output; configured in `appsettings.Development.json` |
| Windows Event Log | When running as a Windows Service | Application log, Source: `FileOrganizer` — view in **Event Viewer** |
| HTML Log Viewer | Always (when `LogFilePath` is set) | Browser-friendly; auto-refreshes every 10 s |

Default log level for all sinks: **Information**.
Set `"Default": "Debug"` under `Logging:LogLevel` in `appsettings.json` for verbose output.

---

## Hot-Reload Configuration

`appsettings.json` is loaded with `reloadOnChange: true`. The following settings take effect **on the next polling cycle** without restarting the service or the process:

- Adding, removing, or disabling rules
- Changing `PollingIntervalSeconds`
- Changing any rule field: `FilePattern`, `SourceFolders`, `TargetFolder`, `Operation`, `DuplicateHandling`, `IncludeSubDirectories`, `MinFileAgeMinutes`, `MaxFileAgeMinutes`
- Changing `ReportFilePath`, `LogFilePath`, `LogMaxEntries`, `LogRetentionHours`

> **Note:** When running as a Windows Service the active config file is `publish\appsettings.json`, not the one in the source repository.
