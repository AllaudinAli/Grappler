#Requires -Version 5.1
<#
    Parallel download engine + live terminal dashboard.

    Dot-sourced by menu.ps1, so it reads $YtDlp, $ConfFile, $FfmpegDir,
    $DownloadFolder, $TempFolder and $JsArgs from there.

    How it works: each download is a separate yt-dlp process with its
    output redirected to a file in a scratch folder. yt-dlp is asked for
    machine-readable progress lines (--progress-template), and the
    dashboard polls those files and redraws itself in place.

    PYTHONUNBUFFERED matters: without it Python block-buffers stdout when
    it isn't a console, and progress would arrive in ~8 KB lumps instead
    of live.
#>

# Fields are @@-separated; the title goes last because it's the only one
# that could plausibly contain the separator.
$ProgressTemplate = 'download:PROG@@%(progress._percent_str)s' +
                    '@@%(progress._speed_str)s' +
                    '@@%(progress._eta_str)s' +
                    '@@%(progress._total_bytes_str,progress._total_bytes_estimate_str)s' +
                    '@@%(info.vcodec)s' +
                    '@@%(info.title)s'


# Start-Process wants one flat string, so anything containing a space has
# to be quoted the way the Windows command line expects.
function Get-ArgLine {
    param([string[]] $ArgList)

    $quoted = foreach ($item in $ArgList) {
        if ($item -match '[\s"]') { '"' + ($item -replace '"', '\"') + '"' }
        else                      { $item }
    }
    return ($quoted -join ' ')
}


function New-DownloadJob {
    param(
        [int]      $Index,
        [string]   $Url,
        [string[]] $ExtraArgs,
        [string]   $WorkDir
    )

    return [pscustomobject]@{
        Index     = $Index
        Url       = $Url
        ExtraArgs = $ExtraArgs
        OutFile   = Join-Path $WorkDir ('job{0}.out' -f $Index)
        ErrFile   = Join-Path $WorkDir ('job{0}.err' -f $Index)
        Process   = $null
        State     = 'queued'      # queued | running | done | failed
        Title     = ''
        Percent   = 0
        Speed     = ''
        Eta       = ''
        Total     = ''
        Stage     = ''
        FinalPath = ''
        ExitCode  = $null
        Error     = ''
    }
}


function Start-DownloadJob {
    param($Job)

    $ytArgs = @(
        '--config-location', $ConfFile,
        '--ffmpeg-location', $FfmpegDir,
        '-P', ('home:' + $DownloadFolder),
        '-P', ('temp:' + $TempFolder),
        '--newline',                       # one progress line at a time, so it can be parsed
        '--color', 'never',                # no escape codes in the status file
        '--progress-template', $ProgressTemplate,
        '--concurrent-fragments', '2'      # lower than single-download mode: N jobs share the pipe
    )
    $ytArgs += $JsArgs
    $ytArgs += $Job.ExtraArgs
    $ytArgs += $Job.Url

    Remove-Item $Job.OutFile, $Job.ErrFile -Force -ErrorAction SilentlyContinue

    # Each job gets its own hidden console. That keeps yt-dlp's
    # --console-title from fighting the other jobs over our window title.
    $Job.Process = Start-Process -FilePath $YtDlp `
        -ArgumentList (Get-ArgLine $ytArgs) `
        -RedirectStandardOutput $Job.OutFile `
        -RedirectStandardError  $Job.ErrFile `
        -WindowStyle Hidden -PassThru

    # Touching .Handle is not pointless: without it .NET discards the
    # process handle on exit and .ExitCode comes back $null for every
    # job, successes included. Reading it here pins the handle so the
    # real exit code is still available after the process ends.
    try { $null = $Job.Process.Handle } catch { }

    $Job.State = 'running'
}


function Update-JobState {
    param($Job)

    if (-not (Test-Path $Job.OutFile)) { return }

    $lines = @()
    try { $lines = @(Get-Content $Job.OutFile -Tail 60 -ErrorAction Stop) } catch { return }

    # Walk forward so the newest marker wins - postprocessing lines always
    # come after the download lines they follow.
    foreach ($line in $lines) {
        if ($line -like 'PROG@@*') {
            $f = $line -split '@@', 7
            if ($f.Count -ge 7) {
                $pct = ($f[1] -replace '[^\d\.]', '')
                if ($pct) { $Job.Percent = [double]$pct }
                $Job.Speed = $f[2].Trim()
                $Job.Eta   = $f[3].Trim()
                $Job.Total = $f[4].Trim()
                if ($f[6].Trim()) { $Job.Title = $f[6].Trim() }
                # vcodec 'none' means this pass is the audio stream
                if ($f[5].Trim() -eq 'none') { $Job.Stage = 'audio' } else { $Job.Stage = 'video' }
            }
            continue
        }
        switch -Regex ($line) {
            '^\[Merger\]'                                  { $Job.Stage = 'merging'   }
            '^\[ExtractAudio\]'                            { $Job.Stage = 'converting'}
            '^\[(Metadata|EmbedThumbnail|ThumbnailsConvertor|EmbedSubtitle)\]' { $Job.Stage = 'tagging' }
            '^\[MoveFiles\]'                               { $Job.Stage = 'saving'    }
            '^ERROR:'                                      { $Job.Error = $line       }
            # The finished size has to come from the file itself. The last
            # progress line only knows about the audio stream, which would
            # report ~4 MB for an 11 MB video.
            '^\[MoveFiles\] Moving file .+ to "(.+)"\s*$'   { $Job.FinalPath = $matches[1] }
        }
    }
}


function Get-JobError {
    param($Job)

    if ($Job.Error) { return $Job.Error }
    foreach ($file in @($Job.ErrFile, $Job.OutFile)) {
        if (-not (Test-Path $file)) { continue }
        $hit = @(Get-Content $file -ErrorAction SilentlyContinue |
                 Where-Object { $_ -match 'ERROR:|error:' } | Select-Object -First 1)
        if ($hit.Count) { return $hit[0].Trim() }
    }
    return 'no error message captured'
}


# ---------- drawing ------------------------------------------------
function Get-Layout {
    $width = 100
    try { $width = [Console]::WindowWidth } catch { }
    if ($width -lt 60) { $width = 60 }

    # Give the title whatever is left after the fixed-width columns.
    $titleWidth = $width - 52
    if ($titleWidth -lt 14) { $titleWidth = 14 }
    if ($titleWidth -gt 40) { $titleWidth = 40 }

    return [pscustomobject]@{ Width = $width; TitleWidth = $titleWidth }
}

function Format-Cell {
    param([string]$Text, [int]$Width)

    if ($null -eq $Text) { $Text = '' }
    if ($Text.Length -gt $Width) {
        if ($Width -le 3) { return $Text.Substring(0, $Width) }
        return $Text.Substring(0, $Width - 3) + '...'
    }
    return $Text.PadRight($Width)
}

function Get-FinishedSize {
    param($Job)

    if (-not $Job.FinalPath) { return '' }
    try {
        $item = Get-Item -LiteralPath $Job.FinalPath -ErrorAction Stop
    } catch {
        return ''
    }
    $mb = $item.Length / 1MB
    if ($mb -ge 1024) { return ('{0:N2} GB' -f ($mb / 1024)) }
    return ('{0:N1} MB' -f $mb)
}

function Format-Bar {
    param([double]$Percent, [int]$Width = 12)

    $filled = [int][Math]::Round(($Percent / 100.0) * $Width)
    if ($filled -lt 0)      { $filled = 0 }
    if ($filled -gt $Width) { $filled = $Width }
    return ('#' * $filled) + ('.' * ($Width - $filled))
}

function Format-JobRow {
    param($Job, $Layout)

    $name = $Job.Title
    if (-not $name) { $name = $Job.Url }
    $cell = Format-Cell -Text $name -Width $Layout.TitleWidth

    switch ($Job.State) {
        'queued' { return '  {0,2}  {1}  queued' -f $Job.Index, $cell }
        'done'   {
            $size = Get-FinishedSize -Job $Job
            if ($size) { return '  {0,2}  {1}  done   {2}' -f $Job.Index, $cell, $size }
            return     '  {0,2}  {1}  done' -f $Job.Index, $cell
        }
        'failed' { return '  {0,2}  {1}  FAILED' -f $Job.Index, $cell }
    }

    # running
    if ($Job.Stage -in @('merging', 'converting', 'tagging', 'saving')) {
        return '  {0,2}  {1}  {2}...' -f $Job.Index, $cell, $Job.Stage
    }
    return '  {0,2}  {1}  [{2}] {3,5:N1}%  {4,10}  {5}' -f `
        $Job.Index, $cell, (Format-Bar $Job.Percent), $Job.Percent, $Job.Speed, $Job.Stage
}

function Get-DashboardLines {
    param($Jobs, [int]$MaxParallel, $Layout)

    $rule  = '  ' + ('-' * ($Layout.Width - 4))
    $done  = @($Jobs | Where-Object { $_.State -eq 'done'    }).Count
    $fail  = @($Jobs | Where-Object { $_.State -eq 'failed'  }).Count
    $run   = @($Jobs | Where-Object { $_.State -eq 'running' }).Count
    $queue = @($Jobs | Where-Object { $_.State -eq 'queued'  }).Count

    $lines = @()
    $lines += ('   {0} downloads, {1} at a time' -f $Jobs.Count, $MaxParallel)
    $lines += $rule

    # Keep the block inside the window: show active work first, then the rest.
    $maxRows = 15
    try { $maxRows = [Math]::Max(4, [Console]::WindowHeight - 9) } catch { }

    $ordered = @($Jobs | Where-Object { $_.State -eq 'running' }) +
               @($Jobs | Where-Object { $_.State -eq 'queued'  }) +
               @($Jobs | Where-Object { $_.State -in @('done', 'failed') })

    if ($ordered.Count -le $maxRows) {
        foreach ($j in ($Jobs | Sort-Object Index)) { $lines += Format-JobRow -Job $j -Layout $Layout }
    } else {
        foreach ($j in ($ordered | Select-Object -First $maxRows)) {
            $lines += Format-JobRow -Job $j -Layout $Layout
        }
        $lines += ('      ...and {0} more' -f ($ordered.Count - $maxRows))
    }

    $lines += $rule
    $lines += ('   done {0}   running {1}   queued {2}   failed {3}' -f $done, $run, $queue, $fail)
    return $lines
}


# ---------- main entry point --------------------------------------
function Invoke-ParallelDownload {
    param(
        [string[]] $Urls,
        [string[]] $ExtraArgs,
        [int]      $MaxParallel = 3,
        [string]   $Label = 'Parallel download'
    )

    $urlList = @($Urls | Where-Object { $_ -match '^https?://' } | Select-Object -Unique)
    if ($urlList.Count -eq 0) {
        Write-Host ''
        Write-Host '   No usable URLs.' -ForegroundColor Yellow
        Write-Host ''
        return
    }

    $workDir = Join-Path $env:TEMP ('vdl-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null

    $jobs = @()
    for ($i = 0; $i -lt $urlList.Count; $i++) {
        $jobs += New-DownloadJob -Index ($i + 1) -Url $urlList[$i] `
                                 -ExtraArgs $ExtraArgs -WorkDir $workDir
    }

    Write-Host ''
    Write-Host ('   ' + $Label) -ForegroundColor Cyan
    Write-Host ''

    # Unbuffered, or progress arrives in lumps instead of live.
    $previousUnbuffered = $env:PYTHONUNBUFFERED
    $env:PYTHONUNBUFFERED = '1'

    $layout        = Get-Layout
    $anchorOk      = $false
    $anchor        = 0
    $lastSignature = ''
    try { $anchor = [Console]::CursorTop; $anchorOk = $true } catch { }

    try {
        while ($true) {
            # top up the running set
            while (@($jobs | Where-Object { $_.State -eq 'running' }).Count -lt $MaxParallel) {
                $next = @($jobs | Where-Object { $_.State -eq 'queued' } | Select-Object -First 1)
                if ($next.Count -eq 0) { break }
                Start-DownloadJob -Job $next[0]
            }

            foreach ($job in @($jobs | Where-Object { $_.State -eq 'running' })) {
                Update-JobState -Job $job
                if ($job.Process.HasExited) {
                    Update-JobState -Job $job      # catch the final lines

                    $code = $null
                    try { $code = $job.Process.ExitCode } catch { }

                    # If the exit code is somehow unreadable, fall back to
                    # asking the log whether anything actually went wrong.
                    if ($null -eq $code) {
                        $code = 0
                        if ($job.Error) { $code = 1 }
                    }

                    $job.ExitCode = $code
                    if ($code -eq 0) {
                        $job.State   = 'done'
                        $job.Percent = 100
                        $job.Stage   = ''
                    } else {
                        $job.State = 'failed'
                        $job.Error = Get-JobError -Job $job
                    }
                }
            }

            $lines = Get-DashboardLines -Jobs $jobs -MaxParallel $MaxParallel -Layout $layout

            if ($anchorOk) {
                try { [Console]::SetCursorPosition(0, $anchor) } catch { $anchorOk = $false }
            }

            $draw = $true
            if (-not $anchorOk) {
                # No in-place redraw available (output is redirected, or the
                # block scrolled away). Printing the whole board every 400ms
                # would flood the screen, so only reprint when a job actually
                # changes state - percentages alone don't count.
                $signature = (($jobs | ForEach-Object {
                    '{0}:{1}:{2}' -f $_.Index, $_.State, $_.Stage
                }) -join '|')
                if ($signature -eq $lastSignature) { $draw = $false }
                $lastSignature = $signature
            }

            if ($draw) {
                foreach ($line in $lines) {
                    Write-Host $line.PadRight($layout.Width - 1)
                }
            }

            $left = @($jobs | Where-Object { $_.State -in @('queued', 'running') }).Count
            if ($left -eq 0) { break }
            Start-Sleep -Milliseconds 400
        }
    } finally {
        # Ctrl+C or a crash shouldn't leave orphaned downloaders behind.
        foreach ($job in $jobs) {
            if ($job.Process -and -not $job.Process.HasExited) {
                try { $job.Process.Kill() } catch { }
                $job.State = 'failed'
                $job.Error = 'stopped before finishing'
            }
        }
        $env:PYTHONUNBUFFERED = $previousUnbuffered
    }

    # ---- summary ----
    $failed = @($jobs | Where-Object { $_.State -eq 'failed' })
    $okay   = @($jobs | Where-Object { $_.State -eq 'done'   })

    Write-Host ''
    Write-Host ('   {0} of {1} downloaded.' -f $okay.Count, $jobs.Count) -ForegroundColor Green
    if ($failed.Count) {
        Write-Host ''
        Write-Host ('   {0} failed:' -f $failed.Count) -ForegroundColor Yellow
        foreach ($job in $failed) {
            $name = $job.Title
            if (-not $name) { $name = $job.Url }
            Write-Host ('     {0}  {1}' -f $job.Index, $name) -ForegroundColor Yellow
            Write-Host ('         ' + (Get-JobError -Job $job)) -ForegroundColor DarkGray
        }
        Write-Host ''
        Write-Host '   Full logs (deleted when you close this window):' -ForegroundColor DarkGray
        Write-Host ('     ' + $workDir) -ForegroundColor DarkGray
        Write-Host ''
        Read-Host '   Press Enter to continue' | Out-Null
    }

    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ''
}
