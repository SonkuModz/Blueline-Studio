@echo off
rem Builds the NHL 19 DLSS emulator on this PC. See README.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-DlssEmulator.ps1" %*
echo.
pause
