@echo off
rem Fab window on the server: SSH tunnel + browser in one click. Details: fab-window.ps1 next to this file.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fab-window.ps1"
if errorlevel 1 pause
