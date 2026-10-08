@echo off
setlocal
title ESDP Code Export
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Export-Code.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%EXIT_CODE%"=="0" echo Export failed. Error code: %EXIT_CODE%
pause
exit /b %EXIT_CODE%
