@echo off
REM IOLSIMULATOR - Configurador de tablets (kit para clinicas, sin Git Bash).
REM Doble clic en este archivo: prepara la consola en UTF-8 (para que se vean
REM bien los acentos y la enie) y ejecuta configurar.ps1 con Windows
REM PowerShell 5.1 (el que ya viene instalado en Windows 10/11).
REM Ver docs/builds-deploy.md "Kit para clinicas (Windows)".

chcp 65001 >nul
title IOLSIMULATOR - Configurar tablet

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0configurar.ps1"

echo.
pause
