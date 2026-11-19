@echo off
setlocal
cd /d "%~dp0"
if not exist "TwinLink.exe" (
  call build.cmd
  if errorlevel 1 (
    pause
    exit /b 1
  )
)
start "" "%~dp0TwinLink.exe"
