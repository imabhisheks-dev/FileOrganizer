@echo off
:: ============================================================
:: remove-startup.bat — Remove the FileOrganizer startup task
:: ============================================================

setlocal

set TASK_NAME=FileOrganizer

echo Stopping FileOrganizer if running...
taskkill /IM FileOrganizer.exe /F >nul 2>&1

echo Removing scheduled task...
schtasks /Delete /TN "%TASK_NAME%" /F
if %ERRORLEVEL% neq 0 (
    echo Task "%TASK_NAME%" not found or already removed.
) else (
    echo Done. FileOrganizer will no longer start at login.
)

endlocal
