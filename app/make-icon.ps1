#Requires -Version 5.1
<#
    Builds app\src\AppIcon.ico (and, with -PngDir, the Brave extension's
    icon-16/32/48/128.png) from the logo files in app\assets.

    The logo comes as several hand-made sizes, named logo-<size>.png
    (logo-48.png, logo-96.png, ...), with transparent backgrounds. For
    each icon size the best one is chosen:

      - an exact match is copied byte for byte - a pixel-perfect 48 stays that way
      - otherwise the smallest logo at least that big is scaled down,
        since shrinking keeps edges crisp
      - only when every logo is smaller is the biggest one scaled up

    To change the logo, replace or add logo-<size>.png files and run
    Build.bat - or this script directly.

    Frames are stored as PNG inside the .ico, which Windows Vista and
    later read natively, so a 256px frame costs a few KB instead of 256 KB.
#>
param(
    [string] $OutPath = (Join-Path $PSScriptRoot 'src\AppIcon.ico'),
    [string] $AssetDir = (Join-Path $PSScriptRoot 'assets'),
    [string] $PreviewPath = '',
    [string] $PngDir = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---- load every logo-<size>.png ------------------------------------------
$sources = @{}
$sourceBytes = @{}
foreach ($file in Get-ChildItem -Path $AssetDir -Filter 'logo-*.png') {
    # Read through a stream so the file is not left locked.
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $image = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream(, $bytes)))
    if ($image.Width -ne $image.Height) { throw "$($file.Name) is not square." }
    $sources[$image.Width] = $image
    $sourceBytes[$image.Width] = $bytes
}
if ($sources.Count -eq 0) { throw "No logo-<size>.png files in $AssetDir" }
$available = @($sources.Keys | Sort-Object)

function Get-Icon {
    param([int] $Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size,
              [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)

    $bigger = @($available | Where-Object { $_ -gt $Size })
    $pick = if ($bigger.Count -gt 0) { $bigger[0] } else { $available[-1] }
    $src = $sources[$pick]
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    # Clamped edges stop the filter from smearing transparency into the
    # outermost pixels.
    $attrs = New-Object System.Drawing.Imaging.ImageAttributes
    $attrs.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)),
                 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $attrs)
    $attrs.Dispose(); $g.Dispose()
    return $bmp
}

<#
    PNG bytes for one icon size. An exact-size logo is passed through as
    its original file: even a nearest-neighbour redraw shifts some
    semi-transparent pixels by a step, and a hand-tuned size should stay
    exactly as it was made.
#>
function Get-IconPng {
    param([int] $Size)
    if ($sourceBytes.ContainsKey($Size)) { return , $sourceBytes[$Size] }
    $bmp = Get-Icon $Size
    $bytes = Get-PngBytes $bmp
    $bmp.Dispose()
    return , $bytes
}

function Get-PngBytes {
    param([System.Drawing.Bitmap] $Bitmap)
    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return , $bytes
}

# ---- the .ico ----------------------------------------------------------
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $png = Get-IconPng $size
    $frames += , $png
    if ($PreviewPath -and $size -eq 256) { [System.IO.File]::WriteAllBytes($PreviewPath, $png) }
}

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$file = [System.IO.File]::Create($OutPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    # ICONDIR
    $writer.Write([UInt16]0)              # reserved
    $writer.Write([UInt16]1)              # type: icon
    $writer.Write([UInt16]$sizes.Count)

    # ICONDIRENTRY per frame - 0 in the size byte means 256.
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dim)         # width
        $writer.Write([byte]$dim)         # height
        $writer.Write([byte]0)            # palette size
        $writer.Write([byte]0)            # reserved
        $writer.Write([UInt16]1)          # colour planes
        $writer.Write([UInt16]32)         # bits per pixel
        $writer.Write([UInt32]$frames[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }

    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

# ---- the extension's PNGs ----------------------------------------------
if ($PngDir) {
    if (-not (Test-Path $PngDir)) { New-Item -ItemType Directory -Path $PngDir -Force | Out-Null }
    foreach ($size in @(16, 32, 48, 128)) {
        [System.IO.File]::WriteAllBytes((Join-Path $PngDir ('icon-{0}.png' -f $size)), (Get-IconPng $size))
    }
}

foreach ($image in $sources.Values) { $image.Dispose() }

Write-Output ("Wrote {0} ({1:N0} bytes, {2} frames from logo sizes {3})" -f `
    $OutPath, (Get-Item $OutPath).Length, $sizes.Count, ($available -join ', '))
