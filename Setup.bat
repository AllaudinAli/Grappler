@echo off
rem ---------------------------------------------------------
rem  First-time setup. Run this once after cloning or
rem  downloading Grappler:
rem    1. downloads yt-dlp, ffmpeg and deno into bin\
rem       (official builds, about 400 MB - nothing is installed
rem       system-wide and PATH is never touched)
rem    2. builds Grappler.exe from app\src
rem
rem  Safe to run again at any time - it repairs or refreshes
rem  everything.
rem ---------------------------------------------------------
title Grappler setup
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup.ps1"
if errorlevel 1 goto failed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\build.ps1"
if errorlevel 1 goto failed

echo.
echo  All set. Double-click Grappler.exe to start.
echo.
echo  Using Brave? Run Install-Brave-Extension.bat for the
echo  browser button - see README.md.
echo.
pause
exit /b 0

:failed
echo.
echo  Setup did not finish. The message above should say why.
echo.
pause
exit /b 1
