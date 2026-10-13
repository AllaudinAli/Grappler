#Requires -Version 5.1
<#
    Connects the Brave extension (browser-extension\) to Grappler.exe.

    Brave only starts programs that are registered as a "native messaging
    host": one registry value under HKEY_CURRENT_USER pointing at
    browser-extension\host\com.grappler.host.json. That is the only thing
    Grappler ever writes outside its own folder, it needs no admin rights,
    and -Uninstall removes it again.

    Chrome's key is set too - Brave builds have read it as well, and it
    makes the extension work in Chrome for free.

        .\scripts\install-extension.ps1              register
        .\scripts\install-extension.ps1 -Uninstall   unregister

    Run it again if the Grappler folder moves.
#>
param([switch] $Uninstall)

$ErrorActionPreference = 'Stop'

$Root      = Split-Path -Parent $PSScriptRoot
$ExtDir    = Join-Path $Root 'browser-extension'
$Manifest  = Join-Path $ExtDir 'host\com.grappler.host.json'
$IconDir   = Join-Path $ExtDir 'icons'
$Exe       = Join-Path $Root 'Grappler.exe'
$HostName  = 'com.grappler.host'

$Keys = @(
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName",
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
)

Write-Host ''

if ($Uninstall) {
    foreach ($key in $Keys) {
        if (Test-Path $key) { Remove-Item $key -Recurse -Force }
    }
    Write-Host '  Unregistered. Remove the extension from brave://extensions too.' -ForegroundColor Green
    Write-Host ''
    exit 0
}

if (-not (Test-Path $Exe)) {
    throw 'Grappler.exe is missing - run Build.bat first.'
}

# The extension's icons are drawn, not checked in - same as the app icon.
if (-not (Test-Path (Join-Path $IconDir 'icon-128.png'))) {
    Write-Host '  drawing extension icons...' -ForegroundColor DarkGray
    $scratchIco = Join-Path $IconDir 'scratch.ico'
    & (Join-Path $Root 'app\make-icon.ps1') -OutPath $scratchIco -PngDir $IconDir | Out-Null
    Remove-Item $scratchIco -Force -ErrorAction SilentlyContinue
}

foreach ($key in $Keys) {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-Item -Path $key -Value $Manifest
}

Write-Host '  Grappler is registered with Brave.' -ForegroundColor Green
Write-Host ''
Write-Host '  If the extension is not loaded yet:' -ForegroundColor Cyan
Write-Host '    1. Open  brave://extensions'
Write-Host '    2. Turn on "Developer mode" (top right)'
Write-Host '    3. Click "Load unpacked" and choose this folder:'
Write-Host ('         ' + $ExtDir) -ForegroundColor White
Write-Host ''
Write-Host '  (The folder path is on your clipboard.)' -ForegroundColor DarkGray
try { Set-Clipboard -Value $ExtDir } catch { }
Write-Host ''
