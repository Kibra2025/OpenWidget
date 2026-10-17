@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
set PUBLISH_RESULT=%ERRORLEVEL%
pause
exit /b %PUBLISH_RESULT%
