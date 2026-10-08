@echo off
setlocal
title ESDP Code Import
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Import-Code.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%EXIT_CODE%"=="0" echo Import failed. Error code: %EXIT_CODE%
pause
exit /b %EXIT_CODE%
