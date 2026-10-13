#Requires -Version 5.1
<#
    Grappler - interactive terminal front-end for yt-dlp.
    Launched by Download.bat in the project root; not meant
    to be run directly (though it works if you do).
#>

$ErrorActionPreference = 'Stop'

# ---------- paths --------------------------------------------------
$Root      = Split-Path -Parent $PSScriptRoot
$YtDlp     = Join-Path $Root 'bin\yt-dlp.exe'
$FfmpegDir = Join-Path $Root 'bin'
$DenoExe   = Join-Path $Root 'bin\deno.exe'
$ConfFile  = Join-Path $Root 'config\yt-dlp.conf'
$SettingsF = Join-Path $Root 'config\settings.ps1'

foreach ($required in @($YtDlp, $ConfFile, $SettingsF)) {
    if (-not (Test-Path $required)) {
        Write-Host ''
        Write-Host "  Missing file: $required" -ForegroundColor Red
        Write-Host '  Run scripts\setup.ps1 to repair the installation.' -ForegroundColor Yellow
        Write-Host ''
        Read-Host '  Press Enter to close'
        exit 1
    }
}

. $SettingsF
if (-not $DownloadFolder) { $DownloadFolder = Join-Path $Root 'downloads' }
if (-not (Test-Path $DownloadFolder)) {
    New-Item -ItemType Directory -Path $DownloadFolder -Force | Out-Null
}
$TempFolder = Join-Path $DownloadFolder '.incomplete'

$YtVersion = 'unknown'
try { $YtVersion = (& $YtDlp --version | Select-Object -First 1) } catch { }

# YouTube needs a JavaScript runtime to work out its stream URLs; without
# one, higher-quality formats quietly go missing. Prefer the copy bundled
# in bin\, fall back to a system-wide node, and carry on without either
# (other sites don't need it).
$JsArgs = @()
if (Test-Path $DenoExe) {
    $JsArgs = @('--js-runtimes', ('deno:' + $DenoExe))
} elseif (Get-Command node -ErrorAction SilentlyContinue) {
    $JsArgs = @('--js-runtimes', 'node')
}

# Sites without a dedicated extractor often sit behind bot checks that
# serve a challenge page ("Unable to extract flashvars"). Looking like
# Chrome gets the real page. Scoped to "generic" so YouTube is untouched.
# Downloads get this from yt-dlp.conf; the probes below ignore the config.
$SiteArgs = @('--extractor-args', 'generic:impersonate=chrome')

# yt-dlp is a frozen Python program, and Python block-buffers stdout
# whenever it isn't writing straight to a console. That would make
# progress arrive in silent lumps, so switch buffering off globally.
$env:PYTHONUNBUFFERED = '1'

# So titles with accents, CJK characters and emoji print correctly
# instead of as question marks.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$script:LastExit = 0
$script:MultiRan = $false
$script:Quitting = $false

. (Join-Path $PSScriptRoot 'parallel.ps1')


# ---------- little helpers ----------------------------------------
function Write-Rule {
    Write-Host ('  ' + ('-' * 60)) -ForegroundColor DarkGray
}

function Get-FreeSpaceText {
    try {
        $qualifier = (Split-Path -Qualifier $DownloadFolder).TrimEnd(':')
        $drive = Get-PSDrive -Name $qualifier -ErrorAction Stop
        return ('{0:N1} GB free on {1}:' -f ($drive.Free / 1GB), $qualifier)
    } catch {
        return ''
    }
}

function Show-Header {
    Clear-Host
    Write-Host ''
    Write-Host '   GRAPPLER' -ForegroundColor Cyan
    Write-Rule
    Write-Host ('   yt-dlp {0}' -f $YtVersion) -ForegroundColor DarkGray
    Write-Host ('   Saving to  {0}' -f $DownloadFolder) -ForegroundColor DarkGray
    $space = Get-FreeSpaceText
    if ($space) { Write-Host ('   Disk       {0}' -f $space) -ForegroundColor DarkGray }
    Write-Rule
    Write-Host ''
}


# ---------- probe a URL for title / length ------------------------
function Get-VideoInfo {
    param([string]$Url)

    $probeArgs = @(
        '--ignore-config', '--no-warnings', '--no-playlist',
        '--playlist-items', '1',
        '--print', '%(title)s',
        '--print', '%(uploader,channel,uploader_id,extractor)s',
        '--print', '%(duration_string,duration)s',
        '--print', '%(resolution)s'
    )
    $probeArgs += $JsArgs
    $probeArgs += $SiteArgs
    $probeArgs += $Url

    $lines = @()
    try { $lines = @(& $YtDlp @probeArgs) } catch { return $null }
    if ($LASTEXITCODE -ne 0 -or $lines.Count -lt 1) { return $null }

    $uploader = ''
    $duration = ''
    $res      = ''
    if ($lines.Count -gt 1) { $uploader = $lines[1] }
    if ($lines.Count -gt 2) { $duration = $lines[2] }
    if ($lines.Count -gt 3) { $res      = $lines[3] }

    return [pscustomobject]@{
        Title      = $lines[0]
        Uploader   = $uploader
        Duration   = $duration
        Resolution = $res
    }
}


# ---------- run an actual download --------------------------------
function Invoke-Download {
    param(
        [string]   $Url,
        [string[]] $ExtraArgs,
        [string]   $Label
    )

    $ytArgs = @(
        '--config-location', $ConfFile,
        '--ffmpeg-location', $FfmpegDir,
        '-P', ('home:'  + $DownloadFolder),
        '-P', ('temp:'  + $TempFolder)
    )
    $ytArgs += $JsArgs
    $ytArgs += $ExtraArgs
    $ytArgs += $Url

    Write-Host ''
    Write-Host ('   ' + $Label) -ForegroundColor Cyan
    Write-Rule
    Write-Host ''

    # No pipeline and no capture here on purpose. Anything that consumes
    # this function's output stream - even "| Out-Null" - swallows every
    # line yt-dlp prints, progress bar included, which is why the exit
    # code is handed back through $script:LastExit instead of returned.
    & $YtDlp @ytArgs
    $exit = $LASTEXITCODE
    $script:LastExit = $exit

    Write-Host ''
    Write-Rule
    if ($exit -eq 0) {
        Write-Host '   Finished.' -ForegroundColor Green
    } else {
        Write-Host ('   yt-dlp stopped with exit code {0}.' -f $exit) -ForegroundColor Yellow
        Write-Host '   Scroll up for the reason. Common fixes:' -ForegroundColor DarkGray
        Write-Host '     - private / region-locked video: nothing to do' -ForegroundColor DarkGray
        Write-Host '     - "unable to extract": run Update.bat, sites change often' -ForegroundColor DarkGray
        Write-Host '     - needs a login: see the Sign-in section of README.md' -ForegroundColor DarkGray
    }
    Write-Host ''
}


# ---------- quality presets ---------------------------------------
# Each is the yt-dlp arguments layered on top of config\yt-dlp.conf.
#   -f  picks which streams,  -S  ranks them (resolution first,
#   then framerate, then prefer mp4 as a tiebreak).
$Presets = [ordered]@{
    '1' = @{
        Label = 'Best quality available'
        Args  = @('-f', 'bv*+ba/b', '-S', 'res,fps,ext:mp4:m4a', '--no-playlist')
    }
    '2' = @{
        Label = '1080p or lower'
        Args  = @('-f', 'bv*[height<=1080]+ba/b[height<=1080]/bv*+ba/b',
                  '-S', 'res,fps,ext:mp4:m4a', '--no-playlist')
    }
    '3' = @{
        Label = '720p or lower'
        Args  = @('-f', 'bv*[height<=720]+ba/b[height<=720]/bv*+ba/b',
                  '-S', 'res,fps,ext:mp4:m4a', '--no-playlist')
    }
    '4' = @{
        Label = '480p or lower'
        Args  = @('-f', 'bv*[height<=480]+ba/b[height<=480]/bv*+ba/b',
                  '-S', 'res,fps,ext:mp4:m4a', '--no-playlist')
    }
    '5' = @{
        Label = 'Audio only - MP3'
        Args  = @('-f', 'ba/b', '-x', '--audio-format', 'mp3',
                  '--audio-quality', '0', '--no-playlist')
    }
    '6' = @{
        Label = 'Audio only - best quality, original format'
        Args  = @('-f', 'ba/b', '-x', '--audio-format', 'best', '--no-playlist')
    }
}


function Show-Menu {
    param($Info)

    if ($Info) {
        Write-Host ('   ' + $Info.Title) -ForegroundColor White
        $meta = @()
        if ($Info.Uploader)   { $meta += $Info.Uploader }
        if ($Info.Duration)   { $meta += $Info.Duration }
        if ($Info.Resolution -and $Info.Resolution -ne 'NA') { $meta += ('up to ' + $Info.Resolution) }
        if ($meta.Count) { Write-Host ('   ' + ($meta -join '  |  ')) -ForegroundColor DarkGray }
        Write-Host ''
    }

    Write-Host '   What do you want?' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '     1) Best quality available            (default)'
    Write-Host '     2) 1080p or lower'
    Write-Host '     3) 720p or lower                     smaller file'
    Write-Host '     4) 480p or lower                     smallest'
    Write-Host '     5) Audio only  ->  MP3'
    Write-Host '     6) Audio only  ->  best quality'
    Write-Host ''
    Write-Host '     7) Show every available format, let me pick'
    Write-Host '     8) Whole playlist / channel'
    Write-Host '     9) Subtitles only, no video'
    Write-Host ''
    Write-Host '     N) New URL          Q) Quit' -ForegroundColor DarkGray
    Write-Host ''
}


# ---------- the two odd-one-out actions ---------------------------
function Invoke-PickFormat {
    param([string]$Url)

    Write-Host ''
    Write-Host '   Reading available formats...' -ForegroundColor DarkGray
    Write-Host ''
    $listArgs = @('--ignore-config', '--ffmpeg-location', $FfmpegDir, '--no-playlist', '-F')
    $listArgs += $JsArgs
    $listArgs += $SiteArgs
    $listArgs += $Url
    & $YtDlp @listArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host '   Could not list formats for that URL.' -ForegroundColor Yellow
        return
    }

    Write-Host ''
    Write-Host '   Type the ID from the leftmost column.' -ForegroundColor DarkGray
    Write-Host '   A row marked "video only" has no sound - add +ba to get audio:' -ForegroundColor DarkGray
    Write-Host '       137+ba     that video stream + the best audio' -ForegroundColor DarkGray
    Write-Host '       22         a row that already has both' -ForegroundColor DarkGray
    Write-Host ''
    $picked = (Read-Host '   Format ID').Trim()
    if (-not $picked) {
        Write-Host '   Nothing entered, going back.' -ForegroundColor DarkGray
        return
    }
    Invoke-Download -Url $Url -Label ('Format ' + $picked) `
        -ExtraArgs @('-f', $picked, '--no-playlist')
}

function Invoke-Playlist {
    param([string]$Url)

    $template = '%(playlist_title,playlist,uploader,extractor)s/%(playlist_index)02d - %(title)s [%(id)s].%(ext)s'
    $archive  = Join-Path $DownloadFolder '.downloaded-archive.txt'

    Write-Host ''
    Write-Host '   Every video goes into its own subfolder, numbered in order.' -ForegroundColor DarkGray
    Write-Host '   Already-downloaded videos are skipped, so you can safely' -ForegroundColor DarkGray
    Write-Host '   re-run this later to pick up new uploads.' -ForegroundColor DarkGray
    Write-Host ''
    $range = (Read-Host '   Which items? (Enter = all, or e.g. 1-10 or 3,7,12)').Trim()

    $plArgs = @(
        '-f', 'bv*+ba/b', '-S', 'res,fps,ext:mp4:m4a',
        '--yes-playlist',
        '-o', $template,
        '--download-archive', $archive
    )
    if ($range) { $plArgs += @('--playlist-items', $range) }

    Invoke-Download -Url $Url -ExtraArgs $plArgs -Label 'Whole playlist / channel'
}

function Invoke-SubsOnly {
    param([string]$Url)
    Invoke-Download -Url $Url -Label 'Subtitles only' -ExtraArgs @(
        '--skip-download', '--write-subs', '--write-auto-subs',
        '--sub-langs', 'all', '--convert-subs', 'srt', '--no-playlist'
    )
}


# ---------- after a download --------------------------------------
function Show-AfterMenu {
    Write-Host '   Enter) another download    O) open folder    Q) quit' -ForegroundColor DarkGray
    $answer = (Read-Host '   >').Trim().ToUpper()
    if ($answer -eq 'O') {
        Start-Process explorer.exe $DownloadFolder
        return 'again'
    }
    if ($answer -eq 'Q') { return 'quit' }
    return 'again'
}


# ---------- several URLs at once ----------------------------------
function Read-PresetChoice {
    Write-Host ''
    Write-Host '   Which quality? This applies to every URL in the batch.' -ForegroundColor Yellow
    Write-Host ''
    foreach ($key in $Presets.Keys) {
        Write-Host ('     {0}) {1}' -f $key, $Presets[$key].Label)
    }
    Write-Host ''
    $pick = (Read-Host '   Choice [1]').Trim()
    if ($pick -eq '') { $pick = '1' }
    if (-not $Presets.Contains($pick)) {
        Write-Host ''
        Write-Host ('   "{0}" is not one of the options.' -f $pick) -ForegroundColor Yellow
        Write-Host ''
        Read-Host '   Press Enter to go back' | Out-Null
        return $null
    }
    return $Presets[$pick]
}

function Read-UrlsPasted {
    Write-Host ''
    Write-Host '   Paste your URLs, one per line.' -ForegroundColor Yellow
    Write-Host '   Press Enter on an empty line when you are done.' -ForegroundColor DarkGray
    Write-Host ''
    $urls = @()
    while ($true) {
        $line = (Read-Host ('   {0,2}' -f ($urls.Count + 1))).Trim().Trim('"')
        if ($line -eq '') { break }
        if ($line -notmatch '^https?://') {
            Write-Host '        ignored, that is not a link' -ForegroundColor DarkGray
            continue
        }
        $urls += $line
    }
    return $urls
}

function Read-UrlsFromFile {
    Write-Host ''
    Write-Host '   Path to a text file with one URL per line.' -ForegroundColor Yellow
    Write-Host '   You can drag the file into this window to fill in the path.' -ForegroundColor DarkGray
    Write-Host ''
    $path = (Read-Host '   File').Trim().Trim('"')
    if (-not (Test-Path $path)) {
        Write-Host ''
        Write-Host '   There is no file at that path.' -ForegroundColor Yellow
        Write-Host ''
        Read-Host '   Press Enter to go back' | Out-Null
        return @()
    }
    $urls = @(Get-Content $path |
              ForEach-Object { $_.Trim().Trim('"') } |
              Where-Object   { $_ -match '^https?://' })
    Write-Host ''
    Write-Host ('   Found {0} URLs in that file.' -f $urls.Count) -ForegroundColor DarkGray
    return $urls
}

function Read-ParallelCount {
    Write-Host ''
    Write-Host '   How many should download at the same time?' -ForegroundColor Yellow
    Write-Host '   More is not always faster - your connection is shared between' -ForegroundColor DarkGray
    Write-Host '   them, and some sites throttle you above 3 or 4.' -ForegroundColor DarkGray
    Write-Host ''
    $answer = (Read-Host '   How many [3]').Trim()
    if ($answer -eq '') { return 3 }

    $count = 0
    if (-not [int]::TryParse($answer, [ref] $count)) { return 3 }
    if ($count -lt 1) { $count = 1 }
    if ($count -gt 8) { $count = 8 }
    return $count
}

# Sets $script:MultiRan rather than returning it - see the note on
# Invoke-Download about not putting values in the output stream.
function Invoke-MultiUrlFlow {
    param([string[]] $Urls)

    $script:MultiRan = $false

    $list = @($Urls | Where-Object { $_ -match '^https?://' } | Select-Object -Unique)
    if ($list.Count -eq 0) {
        Write-Host ''
        Write-Host '   No URLs to download.' -ForegroundColor Yellow
        Write-Host ''
        Read-Host '   Press Enter to go back' | Out-Null
        return
    }

    Show-Header
    Write-Host ('   {0} URLs ready to download.' -f $list.Count) -ForegroundColor White
    $preset = Read-PresetChoice
    if (-not $preset) { return }

    $parallel = Read-ParallelCount
    if ($parallel -gt $list.Count) { $parallel = $list.Count }

    Show-Header
    Invoke-ParallelDownload -Urls $list -ExtraArgs $preset.Args `
                            -MaxParallel $parallel -Label $preset.Label
    $script:MultiRan = $true
}


# ================= main loop ======================================
while ($true) {

    Show-Header
    Write-Host '   Paste a video URL, or pick a letter:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '     M) several URLs at once, downloaded in parallel'
    Write-Host '     F) load a text file full of URLs'
    Write-Host '     Q) quit'
    Write-Host ''
    $url = (Read-Host '   URL').Trim().Trim('"')

    if ($url -eq '') { continue }

    switch ($url.ToUpper()) {
        'Q' { $script:Quitting = $true }
        'M' {
            Invoke-MultiUrlFlow -Urls (Read-UrlsPasted)
            if ($script:MultiRan -and (Show-AfterMenu) -eq 'quit') { $script:Quitting = $true }
        }
        'F' {
            Invoke-MultiUrlFlow -Urls (Read-UrlsFromFile)
            if ($script:MultiRan -and (Show-AfterMenu) -eq 'quit') { $script:Quitting = $true }
        }
    }
    if ($script:Quitting) { break }
    if ($url.ToUpper() -in @('M', 'F')) { continue }

    if ($url -notmatch '^https?://') {
        Write-Host ''
        Write-Host "   That doesn't look like a link - it should start with http." -ForegroundColor Yellow
        Write-Host ''
        Read-Host '   Press Enter to try again' | Out-Null
        continue
    }

    Show-Header
    Write-Host '   Reading video info...' -ForegroundColor DarkGray
    $info = Get-VideoInfo -Url $url

    Show-Header
    if (-not $info) {
        Write-Host "   Couldn't read details for that URL." -ForegroundColor Yellow
        Write-Host '   Trying to download anyway usually still works.' -ForegroundColor DarkGray
        Write-Host ''
    }
    Show-Menu -Info $info

    $choice = (Read-Host '   Choice [1]').Trim().ToUpper()
    if ($choice -eq '') { $choice = '1' }

    if ($choice -eq 'Q') { break }
    if ($choice -eq 'N') { continue }

    $handled = $true
    if ($Presets.Contains($choice)) {
        $preset = $Presets[$choice]
        Invoke-Download -Url $url -ExtraArgs $preset.Args -Label $preset.Label
    }
    elseif ($choice -eq '7') { Invoke-PickFormat -Url $url }
    elseif ($choice -eq '8') { Invoke-Playlist   -Url $url }
    elseif ($choice -eq '9') { Invoke-SubsOnly   -Url $url }
    else {
        $handled = $false
        Write-Host ''
        Write-Host ('   "{0}" is not one of the options.' -f $choice) -ForegroundColor Yellow
        Write-Host ''
        Read-Host '   Press Enter to go back' | Out-Null
    }

    if ($handled) {
        if ((Show-AfterMenu) -eq 'quit') { break }
    }
}

Write-Host ''
Write-Host '   Bye.' -ForegroundColor Cyan
Write-Host ''
