@echo off
if exist "%ProgramData%\FocusBlock\Focus.exe" (
  "%ProgramData%\FocusBlock\Focus.exe" --uninstall
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\uninstall.ps1"
)
