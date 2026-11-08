@echo off
rem BiliFluent one-click installer. Double-click this file (do NOT "Run as administrator").
rem All prompts are shown by Install-BiliFluent.ps1; this file only starts 64-bit Windows PowerShell.
setlocal
cd /d "%~dp0"
title BiliFluent Setup
if not exist "%~dp0Install-BiliFluent.ps1" (
  echo [X] Install-BiliFluent.ps1 not found.
  echo     Please extract the WHOLE zip to a normal folder first, then double-click this file again.
  echo.
  pause
  exit /b 1
)
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-BiliFluent.ps1"
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
