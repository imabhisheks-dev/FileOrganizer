# FileOrganizer

A .NET 8 background application that watches one or more source folders for files matching configurable patterns and automatically **moves**, **copies**, or **deletes** them based on per-rule settings. All activity and log output are rendered as self-refreshing HTML files you can open in any browser, and a live dashboard is available at `http://localhost:5051/`.

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
5. [Ways to Run](#ways-to-run)
   - [1. Console mode (dotnet run)](#1-console-mode-dotnet-run)
   - [2. Task Scheduler (recommended)](#2-task-scheduler-recommended)
   - [3. Windows Service](#3-windows-service)
6. [Debugging in VS Code](#debugging-in-vs-code)
7. [HTML Reports](#html-reports)
   - [Activity Report](#activity-report)
   - [Log Viewer](#log-viewer)
   - [Persistence across restarts](#persistence-across-restarts)
8. [Live Dashboard](#live-dashboard)
9. [Logging](#logging)
10. [Hot-Reload Configuration](#hot-reload-configuration)
11. [Troubleshooting](#troubleshooting)

---

## Features

- **Multiple file rules** — define any number of rules, each with its own patterns, source folders, target folder, and operation.
- **Move, Copy, or Delete** — configurable per rule.
- **Multiple file patterns per rule** — e.g. `["*.dmp", "*.log"]`; patterns are union-matched and deduplicated. Strict extension matching avoids Windows 8.3 filename quirks.
- **Sub-directory scanning** — optionally recurse into sub-folders.
- **Duplicate handling** — Skip / Overwrite / Rename (timestamp-suffixed) per rule.
- **Skip if same size** — avoid re-copying files that already exist at the destination with identical size.
- **File age guards** — two independent per-rule age filters:
  - `MostRecentAgeDays` — only process files modified/created within the last N days.
  - `OlderThanDays` — only process files whose most-recent change is older than N days (useful for Delete rules).
- **Prepend source folder name** — optionally prefix the destination file name with the last segment of the source folder path (e.g. `Partners-filename.dmp`).
- **Activity Report** — self-refreshing HTML showing all operations within the retention window, colour-coded by status. Columns are drag-resizable. Persisted across restarts.
- **Log Viewer** — self-refreshing HTML showing all log messages within the retention window, with per-level filter buttons. Persisted across restarts.
- **Live Dashboard** — lightweight HTTP dashboard on `localhost:{DashboardPort}` showing service status, recent errors, per-target-folder file listings with clipboard copy, and **quick-open links** for the Activity Report and Log Viewer.
- **Windows toast notifications** — optional per-rule Windows desktop notification after each successful Copy, Move, or Delete (Task Scheduler mode only).
- **Hot-reload config** — edit `appsettings.json` while the app is running; all changes take effect on the next polling cycle.
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
├── schedule-startup.bat                Publishes + registers Task Scheduler startup task
├── remove-startup.bat                  Removes the Task Scheduler startup task
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
| Operating System | Windows 10 (version 1809 / build 17763) or later |

Install the .NET SDK from https://dotnet.microsoft.com/download.

---

## Configuration Reference

All settings live under the `"FileOrganizer"` section in `appsettings.json`.

### Top-level settings

| Key | Type | Default | Description |
|---|---|---|---|
| `PollingIntervalSeconds` | `int` | `60` | How often (seconds) the service scans all source folders. |
| `ReportFilePath` | `string` | `"FileOrganizer_Report.html"` | Full path of the HTML **activity report** file. Leave empty to disable. |
| `LogFilePath` | `string` | `"FileOrganizer_Log.html"` | Full path of the HTML **log viewer** file. Leave empty to disable. |
| `LogMaxEntries` | `int` | `200` | Maximum number of log entries kept in memory and the HTML log viewer. |
| `LogRetentionDays` | `int` | `7` | Drop log entries older than this many days. `0` = keep forever. |
| `ReportRetentionDays` | `int` | `7` | Drop activity report records older than this many days. `0` = keep forever. |
| `HtmlRefreshSeconds` | `int` | `30` | Browser auto-refresh interval for both HTML files (seconds). |
| `DashboardPort` | `int` | `5051` | Port the live Dashboard listens on (`localhost` only). Requires a restart to change. |

### FileRule settings

Each object in the `"Rules"` array supports:

| Key | Type | Default | Description |
|---|---|---|---|
| `Name` | `string` | `""` | Friendly name shown in logs and reports. |
| `Enabled` | `bool` | `true` | Set to `false` to skip this rule without removing it. |
| `FilePatterns` | `string[]` | `[]` | One or more glob patterns, e.g. `["*.dmp", "*.log"]`, `["TFADO-*-logs.xlsx"]`. Files matching any pattern are processed. |
| `SourceFolders` | `string[]` | `[]` | Folders to scan. UNC paths (`\\server\share\...`) are supported. |
| `TargetFolder` | `string` | `""` | Destination folder (not required for `Delete`). Created automatically if absent. |
| `Operation` | `"Copy"`, `"Move"`, or `"Delete"` | `"Copy"` | Action to perform on matched files. |
| `IncludeSubDirectories` | `bool` | `false` | When `true`, sub-directories inside each `SourceFolder` are also scanned. |
| `DuplicateHandling` | `"Skip"`, `"Overwrite"`, or `"Rename"` | `"Rename"` | What to do when a file with the same name already exists in the target. `Rename` appends a timestamp. Not applicable for `Delete`. |
| `SkipIfSameSize` | `bool` | `false` | When `true`, skip copying/moving if the destination file already exists with the same byte size. |
| `MostRecentAgeDays` | `double` | `0` | Only process files modified/created within this many days. `0` = disabled. Fractional values supported (e.g. `0.5` = 12 h). |
| `OlderThanDays` | `double` | `0` | Only process files older than this many days. `0` = disabled. Useful for Delete rules. |
| `PrependSourceFolderName` | `bool` | `false` | When `true`, the last path segment of the source folder is prepended to the destination file name (e.g. `Partners-file.dmp`). |
| `NotifyOnAction` | `bool` | `false` | When `true`, a Windows toast notification is shown after each successful operation for this rule. Works in Task Scheduler mode (runs as your user). Has no effect when running as a Windows Service in Session 0. |

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
- Both use `max(LastWriteTime, CreationTime)`.
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
        "PrependSourceFolderName": true,
        "SkipIfSameSize": true,
        "NotifyOnAction": true
      },
      {
        "Name": "SyncReports",
        "Enabled": true,
        "FilePatterns": [ "TFADO-*-logs.xlsx" ],
        "SourceFolders": [ "C:\\Users\\username\\Downloads" ],
        "TargetFolder": "C:\\Users\\username\\Documents\\RCAs",
        "Operation": "Copy",
        "IncludeSubDirectories": false,
        "DuplicateHandling": "Overwrite",
        "MostRecentAgeDays": 0,
        "OlderThanDays": 0,
        "PrependSourceFolderName": false,
        "SkipIfSameSize": true,
        "NotifyOnAction": false
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
        "PrependSourceFolderName": false,
        "SkipIfSameSize": false,
        "NotifyOnAction": false
      }
    ]
  }
}
```

---

## Ways to Run

There are three ways to run FileOrganizer. Choose based on your needs:

| | Method | Network shares (UNC) | Auto-start | Best for |
|---|---|---|---|---|
| **1** | `dotnet run` | ✅ | ❌ | Development / debugging |
| **2** | Task Scheduler | ✅ | ✅ at login | **Recommended for everyday use** |
| **3** | Windows Service | ❌ by default | ✅ at boot | Headless / server machines |

---

### 1. Console mode (dotnet run)

```powershell
cd C:\path\to\FileOrganizer
dotnet run
```

Press `Ctrl+C` to stop. Live logs are printed to the terminal.

> `DOTNET_ENVIRONMENT` defaults to `Development` via `dotnet run`, which activates `appsettings.Development.json` and enables `Debug`-level console logging.

---

### 2. Task Scheduler (recommended)

Runs silently in the background under your own Windows account — so UNC network shares work automatically with no credential setup.

**Install (one-time):**

```bat
schedule-startup.bat
```

What the script does:
1. **Stops any running instance** (`schtasks /End` + `taskkill`) and waits 2 seconds for file handles to release.
2. Publishes a self-contained `win-x64` binary to the `publish\` subfolder.
3. Creates a `run-hidden.vbs` launcher so no console window appears.
4. Registers a Task Scheduler task that starts FileOrganizer automatically every time you log in.
5. **Starts the app immediately** via `schtasks /Run` — no relogin required.

**Start immediately (without rebooting):**

```powershell
schtasks /Run /TN FileOrganizer
```

**Check if running:**

```powershell
tasklist /FI "IMAGENAME eq FileOrganizer.exe" /NH
```

**Stop:**

```powershell
taskkill /IM FileOrganizer.exe /F
```

**Remove (uninstall):**

```bat
remove-startup.bat
```

The active configuration file is:
```
<repo>\publish\appsettings.json
```

Edit that file to change rules or paths — hot-reload applies, no restart needed.

---

### 3. Windows Service

Runs at system boot before any user logs in. By default it runs under `LocalSystem` which **cannot access UNC network paths**. If you need network access you must configure a domain service account via `services.msc` → **Log On** tab after installation.

**Install (requires Administrator):**

```bat
install-service.bat
```

What the script does:
1. **Stops any running instance** (`sc stop` + `taskkill`) and waits 2 seconds for file handles to release.
2. Publishes a self-contained `win-x64` binary to the `publish\` subfolder.
3. Registers and starts the Windows Service.

**Control:**

```powershell
Start-Service FileOrganizer
Stop-Service  FileOrganizer
Get-Service   FileOrganizer
```

**Uninstall (requires Administrator):**

```bat
uninstall-service.bat
```

---

## Debugging in VS Code

A `launch.json` and `tasks.json` are included in `.vscode/`.

| Config | How |
|---|---|
| **Debug FileOrganizer** | Press **F5** — builds a Debug binary and launches with the debugger attached. `DOTNET_ENVIRONMENT=Development` is set automatically. |
| **Attach to FileOrganizer** | Start the app in a terminal (`dotnet run`), then use **Attach to FileOrganizer**. |

Useful breakpoints:
- `FileProcessorService.cs` → `ProcessFileAsync()` — see why each file is skipped or processed.
- `ActivityReportService.cs` → `AddRecord()` — confirm a file operation was recorded.

To get verbose log output without a debugger, set in `appsettings.json`:
```json
"LogLevel": { "Default": "Debug" }
```

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
Columns can be **drag-resized** by grabbing the right edge of any header.

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

Both the activity report and log viewer survive restarts. Records are persisted to JSON sidecar files next to the HTML outputs:

| Sidecar file | Contents |
|---|---|
| `FileOrganizerReport.records.json` | Activity report records |
| `FileOrganizerLog.entries.json` | Log viewer entries |

On startup these are loaded back automatically. Old entries outside the retention window are evicted on load.

---

## Live Dashboard

Open `http://localhost:{DashboardPort}/` (default: http://localhost:5051/) in a browser while the app is running.

- **Status pills** — Service Running / Offline, and recent error count (last 5 min)
- **Summary cards** — total files, number of folders, error count, poll interval
- **Reports card** — quick-open buttons for the Activity Report and Log Viewer (opens in a new browser tab)
- **Per-folder tables** — one card per target folder, displayed side by side
  - Columns: Last Modified, Copy Path, File Name, Size
  - Click **⎘** on any row to copy the full file path to clipboard
  - Tables are scrollable (max ~12 rows before scrollbar appears)
  - Column headers are sortable
- **Auto-polls every 5 seconds**
- **JSON API** at `http://localhost:5051/api/status`
- **Report endpoints:** `http://localhost:5051/report` and `http://localhost:5051/log` serve the HTML report files directly

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
- Any rule field: `FilePatterns`, `SourceFolders`, `TargetFolder`, `Operation`, `DuplicateHandling`, `IncludeSubDirectories`, `MostRecentAgeDays`, `OlderThanDays`, `PrependSourceFolderName`, `SkipIfSameSize`, `NotifyOnAction`
- `ReportFilePath`, `LogFilePath`, `LogMaxEntries`, `LogRetentionDays`, `ReportRetentionDays`, `HtmlRefreshSeconds`

> **Note:** `DashboardPort` requires a restart to take effect.  
> When using Task Scheduler or Windows Service, the active config is `publish\appsettings.json`, not the source repo copy.

---

## Troubleshooting

### UNC network paths not accessible when running as Windows Service

**Symptom:** Files on `\\server\share\...` are not found or access is denied, but the same paths work fine with `dotnet run`.

**Cause:** Windows Services run under `LocalSystem` by default, which has no domain credentials and cannot access network shares.

**Fix:** Use **Task Scheduler** (recommended) instead — it runs under your own login and has full network access automatically. If you must use the Windows Service, change the service account in `services.msc` → **File Organizer Service** → Properties → **Log On** tab → **This account** → enter your domain credentials (`DOMAIN\username`).

To find your exact domain username, run:
```powershell
whoami
```

---

### Task Scheduler launches a console window

**Symptom:** Running `schtasks /Run /TN FileOrganizer` or logging in opens a visible console window.

**Cause:** The task is configured to run `FileOrganizer.exe` directly. Console apps always show a window unless launched via `wscript.exe`.

**Fix:** Edit the task in Task Scheduler UI (`taskschd.msc`) → **FileOrganizer** → Properties → **Actions** tab → Edit:

| Field | Value |
|---|---|
| Program/script | `wscript.exe` |
| Add arguments | `"C:\as7.workspace\Repositories\FileOrganizer\publish\run-hidden.vbs"` |
| Start in | `C:\as7.workspace\Repositories\FileOrganizer\publish` |

The `run-hidden.vbs` file is included in the `publish\` folder and launches the exe with window style 0 (invisible).

---

### `schtasks /Create` or `schtasks /Delete` returns "Access is denied"

**Symptom:** Running `schedule-startup.bat` or any `schtasks` command fails with `ERROR: Access is denied`.

**Cause:** Group Policy on your machine restricts task creation via the command line.

**Fix:** Use the **Task Scheduler UI** (`taskschd.msc`) directly to create or edit the task. The `run-hidden.vbs` approach above works fine from the UI even when the command line is blocked.

---

### App exits immediately when started via Task Scheduler

**Symptom:** `schtasks /Run /TN FileOrganizer` reports success but the process disappears within seconds.

**Cause:** `UseWindowsService()` in .NET makes the host wait for a signal from the Windows Service Control Manager. When launched any other way, it exits immediately.

**Fix:** Already resolved in this codebase — `UseConsoleLifetime()` is registered alongside `UseWindowsService()` so the app stays running when started outside of the SCM. If you see this after a fresh clone, ensure `Program.cs` contains both:
```csharp
.UseWindowsService(options => options.ServiceName = "FileOrganizer")
.UseConsoleLifetime(options => options.SuppressStatusMessages = true)
```

---

### Wrong domain in service account

**Symptom:** `services.msc` or `sc create` rejects credentials with "The account name is invalid or does not exist".

**Fix:** Run `whoami` in a terminal to get your exact domain and username, then use that value exactly (e.g. `infocorp\abhishek.singh7`, not `THINKFOLIO\...`). The domain shown in `whoami` is authoritative.

---

### Dashboard shows "Service Offline"

**Symptom:** `http://localhost:5051/` shows the service as offline.

**Checks:**
1. Verify the process is running: `tasklist /FI "IMAGENAME eq FileOrganizer.exe" /NH`
2. Confirm the port matches `appsettings.json` → `DashboardPort`
3. Check the HTML log viewer for startup errors
4. If the process is not running, start it: `schtasks /Run /TN FileOrganizer`

---

### Publish fails because the exe is locked

**Symptom:** `dotnet publish` fails with "The file is locked by: FileOrganizer".

**Fix:** `schedule-startup.bat` and `install-service.bat` both handle this automatically — they stop the running instance before publishing. If running `dotnet publish` manually, stop the app first:
```powershell
taskkill /IM FileOrganizer.exe /F
```
