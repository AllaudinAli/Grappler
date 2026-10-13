@echo off
rem ---------------------------------------------------------
rem  Updates yt-dlp. Run this whenever a site stops working -
rem  it is almost always the fix.
rem ---------------------------------------------------------
title Update yt-dlp
cd /d "%~dp0"

echo.
echo  Checking for a newer yt-dlp...
echo.

"%~dp0bin\yt-dlp.exe" --update

echo.
echo  ---------------------------------------------------------
echo  If a site still fails after this, refresh ffmpeg too:
echo      powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
echo  ---------------------------------------------------------
echo.
pause
