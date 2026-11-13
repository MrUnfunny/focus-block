@echo off
rem Builds dist\FocusSetup.exe from source and opens it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\build.ps1" || exit /b 1
start "" "%~dp0dist\FocusSetup.exe"
