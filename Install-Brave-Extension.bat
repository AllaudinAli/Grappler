@echo off
rem ---------------------------------------------------------
rem  Connects the Brave extension to Grappler. Run it once,
rem  and again if you move the Grappler folder.
rem
rem  To undo:  Install-Brave-Extension.bat -Uninstall
rem ---------------------------------------------------------
title Grappler for Brave
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-extension.ps1" %*

if errorlevel 1 (
    echo.
    echo  Setup failed. The message above should say why.
    echo.
)
pause
