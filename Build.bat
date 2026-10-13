@echo off
rem ---------------------------------------------------------
rem  Rebuilds Grappler.exe from app\src.
rem  Only needed if you change the source - the built app is
rem  already in this folder.
rem ---------------------------------------------------------
title Building Grappler
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\build.ps1" %*

if errorlevel 1 (
    echo.
    echo  The build failed. The message above should say why.
    echo.
)
pause
