@echo off
:: ============================================================
:: uninstall-service.bat — Stop and remove FileOrganizer
:: Run as Administrator
:: ============================================================

setlocal

set SERVICE_NAME=FileOrganizer

echo [1/2] Stopping service...
sc stop "%SERVICE_NAME%"
timeout /t 3 /nobreak >nul

echo [2/2] Removing service...
sc delete "%SERVICE_NAME%"
if %ERRORLEVEL% neq 0 (
    echo ERROR: sc delete failed. Make sure you are running as Administrator.
    exit /b %ERRORLEVEL%
)

echo.
echo Service "%SERVICE_NAME%" has been removed.
echo The publish folder and appsettings.json remain on disk.
echo.
endlocal
