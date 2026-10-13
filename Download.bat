@echo off
rem ---------------------------------------------------------
rem  Grappler terminal menu - double-click this file.
rem ---------------------------------------------------------
title Grappler - terminal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\menu.ps1"

if errorlevel 1 (
    echo.
    echo  The downloader closed unexpectedly. The message above
    echo  should say why.
    echo.
    pause
)
