@echo off
:: ============================================================
:: install-service.bat — Build and register FileOrganizer
:: Run as Administrator
:: ============================================================

setlocal

set SERVICE_NAME=FileOrganizer
set SERVICE_DISPLAY=File Organizer Service
set SERVICE_DESC=Monitors configured source folders and moves/copies matched files to target folders.
set PUBLISH_DIR=%~dp0publish

echo [1/3] Publishing application...
dotnet publish "%~dp0FileOrganizer.csproj" ^
  --configuration Release ^
  --runtime win-x64 ^
  --self-contained true ^
  --output "%PUBLISH_DIR%"
if %ERRORLEVEL% neq 0 (
    echo ERROR: dotnet publish failed.
    exit /b %ERRORLEVEL%
)

echo [2/3] Registering Windows Service...
sc create "%SERVICE_NAME%" ^
  binPath= "%PUBLISH_DIR%\FileOrganizer.exe" ^
  DisplayName= "%SERVICE_DISPLAY%" ^
  start= auto
if %ERRORLEVEL% neq 0 (
    echo ERROR: sc create failed. Make sure you are running as Administrator.
    exit /b %ERRORLEVEL%
)

sc description "%SERVICE_NAME%" "%SERVICE_DESC%"
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/10000/restart/30000

echo [3/3] Starting service...
sc start "%SERVICE_NAME%"
if %ERRORLEVEL% neq 0 (
    echo WARNING: Service registered but could not be started automatically.
    echo          Use: sc start %SERVICE_NAME%
)

echo.
echo Done! Service "%SERVICE_NAME%" is installed and running.
echo Configuration file: %PUBLISH_DIR%\appsettings.json
echo Logs: Windows Event Viewer ^> Application ^> Source: FileOrganizer
echo.
endlocal
