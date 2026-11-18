@echo off
title CAD^&PILLAR MCP - install
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1" %*
echo.
pause
