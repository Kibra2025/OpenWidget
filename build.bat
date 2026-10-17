@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -RunTests
set BUILD_RESULT=%ERRORLEVEL%
pause
exit /b %BUILD_RESULT%
