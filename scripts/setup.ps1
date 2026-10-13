#Requires -Version 5.1
<#
    Downloads the tools Grappler runs on into bin\ - run by
    Setup.bat:
        yt-dlp.exe               the download engine
        ffmpeg.exe / ffprobe.exe merging and format conversion
        deno.exe                 JavaScript runtime YouTube needs

    Tools already in bin\ are left alone, so running Setup.bat
    again (say, after a git pull) is quick. -Force downloads
    them all again, which also repairs a broken copy. The
    engine keeps itself current from Grappler's Settings.

    Nothing is installed system-wide and PATH is never touched.

    Usage:
        powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
        ... -Force          download everything again
        ... -SkipFfmpeg     leave ffmpeg alone (likewise -SkipYtDlp, -SkipDeno)
#>

param(
    [switch] $Force,
    [switch] $SkipYtDlp,
    [switch] $SkipFfmpeg,
    [switch] $SkipDeno
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # makes downloads much faster
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root   = Split-Path -Parent $PSScriptRoot
$BinDir = Join-Path $Root 'bin'

$YtDlpUrl  = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe'
$FfmpegUrl = 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
$DenoUrl   = 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip'

# Pulls named .exe files out of a downloaded .zip straight into bin\
function Expand-ExeFromZip {
    param([string]$ZipPath, [string[]]$ExeNames)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($exe in $ExeNames) {
            $entry = $zip.Entries | Where-Object { $_.Name -eq $exe } | Select-Object -First 1
            if (-not $entry) { throw "$exe was not in the archive." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile(
                $entry, (Join-Path $BinDir $exe), $true)
            Show-Size (Join-Path $BinDir $exe) $exe
        }
    } finally { $zip.Dispose() }
}

foreach ($dir in @($BinDir, (Join-Path $Root 'config'), (Join-Path $Root 'downloads'))) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

function Show-Size {
    param([string]$Path, [string]$Name)
    Write-Host ('    {0,-14} {1,7:N1} MB' -f $Name, ((Get-Item $Path).Length / 1MB)) -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '  Grappler - setup' -ForegroundColor Cyan
Write-Host ('  ' + ('-' * 46)) -ForegroundColor DarkGray

# Already there and not forced: nothing to do.
function Test-Have { param([string[]] $Names)
    if ($Force) { return $false }
    foreach ($n in $Names) { if (-not (Test-Path (Join-Path $BinDir $n))) { return $false } }
    return $true
}
if (Test-Have 'yt-dlp.exe')               { $SkipYtDlp = $true }
if (Test-Have 'ffmpeg.exe', 'ffprobe.exe') { $SkipFfmpeg = $true }
if (Test-Have 'deno.exe')                 { $SkipDeno = $true }

# ---- yt-dlp -------------------------------------------------------
if ($SkipYtDlp) {
    Write-Host '  yt-dlp: already here.' -ForegroundColor DarkGray
} else {
    Write-Host '  Downloading yt-dlp...' -ForegroundColor White
    Invoke-WebRequest -Uri $YtDlpUrl -OutFile (Join-Path $BinDir 'yt-dlp.exe')
    Show-Size (Join-Path $BinDir 'yt-dlp.exe') 'yt-dlp.exe'
}

# ---- ffmpeg -------------------------------------------------------
if ($SkipFfmpeg) {
    Write-Host '  ffmpeg: already here.' -ForegroundColor DarkGray
} else {
    Write-Host '  Downloading ffmpeg (~160 MB, this is the slow part)...' -ForegroundColor White
    $zipPath = Join-Path $env:TEMP ('ffmpeg-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest -Uri $FfmpegUrl -OutFile $zipPath
        Expand-ExeFromZip -ZipPath $zipPath -ExeNames 'ffmpeg.exe', 'ffprobe.exe'
    } finally {
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    }
}

# ---- deno ---------------------------------------------------------
# YouTube signs its stream URLs with JavaScript that has to be executed
# to be understood. Without a JS runtime yt-dlp still works, but the
# higher-quality formats silently disappear.
if ($SkipDeno) {
    Write-Host '  deno: already here.' -ForegroundColor DarkGray
} else {
    Write-Host '  Downloading deno (JavaScript runtime for YouTube)...' -ForegroundColor White
    $zipPath = Join-Path $env:TEMP ('deno-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest -Uri $DenoUrl -OutFile $zipPath
        Expand-ExeFromZip -ZipPath $zipPath -ExeNames 'deno.exe'
    } finally {
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    }
}

# ---- verify -------------------------------------------------------
Write-Host ('  ' + ('-' * 46)) -ForegroundColor DarkGray
$ok = $true
foreach ($exe in 'yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe', 'deno.exe') {
    $path = Join-Path $BinDir $exe
    if (Test-Path $path) {
        Write-Host ('  OK      ' + $exe) -ForegroundColor Green
    } else {
        Write-Host ('  MISSING ' + $exe) -ForegroundColor Red
        $ok = $false
    }
}

Write-Host ''
if ($ok) {
    Write-Host '  Binaries ready.' -ForegroundColor Cyan
} else {
    Write-Host '  Setup incomplete - see the missing files above.' -ForegroundColor Yellow
    exit 1
}
Write-Host ''
