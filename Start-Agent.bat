@echo off
rem ============================================================================
rem  ux-agent launcher (ASCII-only path on purpose: a Chinese file name in the
rem  elevated relaunch command gets mangled by the console code page).
rem ============================================================================
setlocal
set "DIR=%~dp0"
set "EXE=%DIR%agent\bin\ux-agent.exe"
set "LOG=%TEMP%\ux-agent\launcher.log"
if not exist "%TEMP%\ux-agent" mkdir "%TEMP%\ux-agent" >nul 2>&1

call :log "launcher start"
call :log "DIR=%DIR%"
call :log "EXE=%EXE%"

if not exist "%EXE%" (
  call :log "ERROR exe not found"
  echo [ux] agent not found:
  echo      %EXE%
  pause
  exit /b 1
)

net session >nul 2>&1
if %errorlevel%==0 goto :elevated

call :log "not elevated, requesting UAC"
echo [ux] requesting administrator rights (UAC)...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
if %errorlevel% neq 0 (
  call :log "elevation launch failed"
  echo [ux] could not request administrator rights.
  echo      Right-click this file and choose "Run as administrator".
  pause
)
exit /b 0

:elevated
call :log "elevated, starting agent now"
echo [ux] starting agent on 127.0.0.1:18946 ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -WindowStyle Hidden -FilePath '%EXE%' -ArgumentList '--port','18946'"
call :log "start issued"
timeout /t 3 >nul
netstat -ano | findstr ":18946" | findstr LISTENING >nul 2>&1
if %errorlevel%==0 (
  call :log "port 18946 listening OK"
  echo [ux] agent is listening on 127.0.0.1:18946
) else (
  call :log "port 18946 NOT listening"
  echo [ux] agent did not start. See log: %LOG%
)
echo.
echo You can close this window. Go back to OpenRevo and press [Refresh].
timeout /t 4 >nul
exit /b 0

:log
if "%~1"=="" exit /b 0
>>"%LOG%" echo(%~1
exit /b 0