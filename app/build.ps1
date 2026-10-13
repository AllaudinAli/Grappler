#Requires -Version 5.1
<#
    Builds Grappler.exe into the project root.

    Uses the MSBuild that ships inside Windows with the .NET Framework, so
    there is nothing to install: no SDK, no NuGet, no network. The one
    catch is that its C# compiler only understands C# 5 - see the note at
    the top of src\Grappler.csproj.

        .\app\build.ps1            build and copy to the project root
        .\app\build.ps1 -Run       ...and launch it
        .\app\build.ps1 -Clean     delete obj\ and bin\ first
#>
param(
    [switch] $Run,
    [switch] $Clean,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$Root    = Split-Path -Parent $PSScriptRoot
$SrcDir  = Join-Path $PSScriptRoot 'src'
$Project = Join-Path $SrcDir 'Grappler.csproj'
$Icon    = Join-Path $SrcDir 'AppIcon.ico'
$MSBuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
$Target  = Join-Path $Root 'Grappler.exe'
$Built   = Join-Path $SrcDir ('bin\{0}\Grappler.exe' -f $Configuration)

if (-not (Test-Path $MSBuild)) {
    throw "MSBuild not found at $MSBuild. This needs the .NET Framework, which is part of Windows."
}

# The app cannot be replaced while it is running, and the error MSBuild
# gives for that is not obvious. Say so plainly instead.
$running = @(Get-Process -Name 'Grappler' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw 'Grappler is running. Close it first, then build again.'
}

if ($Clean) {
    foreach ($dir in @((Join-Path $SrcDir 'obj'), (Join-Path $SrcDir 'bin'))) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
    Write-Host '  cleaned' -ForegroundColor DarkGray
}

# Icons are made from app\assets on every build, so a new logo shows up
# everywhere at once - the app and the browser extension alike.
Write-Host '  making icons...' -ForegroundColor DarkGray
& (Join-Path $PSScriptRoot 'make-icon.ps1') -PngDir (Join-Path $Root 'browser-extension\icons') | Out-Null

Write-Host ''
Write-Host ('  Building {0}...' -f $Configuration) -ForegroundColor Cyan

$output = & $MSBuild $Project /nologo /v:m /p:Configuration=$Configuration 2>&1
$buildExit = $LASTEXITCODE

# MSB3644 (no v4.8 targeting pack) and MSB3270 (MSIL vs AMD64) are
# unavoidable when building against the GAC instead of reference
# assemblies, and neither affects the output. Everything else is real.
$noise = 'MSB3644|MSB3270|^\s*$|^Build succeeded|^\s+\d+ Warning|^\s+\d+ Error|Time Elapsed'
$interesting = @($output | Where-Object { $_ -notmatch $noise })

foreach ($line in $interesting) {
    if ($line -match ': error ')       { Write-Host ('  ' + $line) -ForegroundColor Red }
    elseif ($line -match ': warning ') { Write-Host ('  ' + $line) -ForegroundColor Yellow }
    else                               { Write-Host ('  ' + $line) -ForegroundColor DarkGray }
}

if ($buildExit -ne 0 -or -not (Test-Path $Built)) {
    Write-Host ''
    Write-Host '  BUILD FAILED' -ForegroundColor Red
    Write-Host ''
    exit 1
}

Copy-Item $Built $Target -Force

$size = (Get-Item $Target).Length / 1KB
Write-Host ''
Write-Host ('  OK  {0}  ({1:N0} KB)' -f $Target, $size) -ForegroundColor Green
Write-Host ''

if ($Run) {
    Start-Process $Target
}
