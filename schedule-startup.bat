@echo off
:: ============================================================
:: schedule-startup.bat — Run FileOrganizer at Windows logon
:: Runs as YOUR user account, so network shares work fine.
:: No admin rights required.
:: ============================================================

setlocal

set TASK_NAME=FileOrganizer
set PUBLISH_DIR=%~dp0publish
set EXE_PATH=%PUBLISH_DIR%\FileOrganizer.exe
set VBS_PATH=%PUBLISH_DIR%\run-hidden.vbs

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

echo [2/3] Writing hidden launcher...
(
echo Dim exe
echo exe = CreateObject^("Scripting.FileSystemObject"^).GetParentFolderName^(WScript.ScriptFullName^) ^& "\FileOrganizer.exe"
echo CreateObject^("WScript.Shell"^).Run """" ^& exe ^& """", 0, False
) > "%VBS_PATH%"

echo [3/3] Registering scheduled task...

:: Remove existing task if present
schtasks /Delete /TN "%TASK_NAME%" /F >nul 2>&1

:: Write a temp XML task definition — launches via wscript so no console window appears
set XML_FILE=%TEMP%\FileOrganizer_task.xml
(
echo ^<?xml version="1.0" encoding="UTF-16"?^>
echo ^<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"^>
echo   ^<Triggers^>
echo     ^<LogonTrigger^>^<Enabled^>true^</Enabled^>^<UserId^>%USERDOMAIN%\%USERNAME%^</UserId^>^</LogonTrigger^>
echo   ^</Triggers^>
echo   ^<Principals^>
echo     ^<Principal id="Author"^>
echo       ^<UserId^>%USERDOMAIN%\%USERNAME%^</UserId^>
echo       ^<LogonType^>InteractiveToken^</LogonType^>
echo       ^<RunLevel^>LeastPrivilege^</RunLevel^>
echo     ^</Principal^>
echo   ^</Principals^>
echo   ^<Settings^>
echo     ^<Hidden^>true^</Hidden^>
echo     ^<DisallowStartIfOnBatteries^>false^</DisallowStartIfOnBatteries^>
echo     ^<StopIfGoingOnBatteries^>false^</StopIfGoingOnBatteries^>
echo     ^<ExecutionTimeLimit^>PT0S^</ExecutionTimeLimit^>
echo     ^<MultipleInstancesPolicy^>IgnoreNew^</MultipleInstancesPolicy^>
echo   ^</Settings^>
echo   ^<Actions^>
echo     ^<Exec^>
echo       ^<Command^>wscript.exe^</Command^>
echo       ^<Arguments^>"%VBS_PATH%"^</Arguments^>
echo       ^<WorkingDirectory^>%PUBLISH_DIR%^</WorkingDirectory^>
echo     ^</Exec^>
echo   ^</Actions^>
echo ^</Task^>
) > "%XML_FILE%"

schtasks /Create /TN "%TASK_NAME%" /XML "%XML_FILE%" /F
del "%XML_FILE%" >nul 2>&1

echo.
echo Done! FileOrganizer will start automatically when you log in to Windows.
echo.
echo To start it right now without rebooting:
echo   schtasks /Run /TN "%TASK_NAME%"
echo.
echo To stop it:
echo   taskkill /IM FileOrganizer.exe /F
echo.
echo To remove it:
echo   remove-startup.bat   (or run: schtasks /Delete /TN "%TASK_NAME%" /F)
echo.
endlocal
