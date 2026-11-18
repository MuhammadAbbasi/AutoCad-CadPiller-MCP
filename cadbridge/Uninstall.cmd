@echo off
title CadBridge - AutoCAD / CAD^&PILLAR MCP - uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1" -Uninstall %*
echo.
pause
