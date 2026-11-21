<#
.SYNOPSIS
Records one window to an MP4 video with ffmpeg Windows Graphics Capture (gfxcapture).

.DESCRIPTION
Captures the content of one window at a fixed frame rate for a fixed duration. Windows
Graphics Capture records each window separately, also when another window covers it,
and it sees Vulkan and OpenGL swapchain output. GDI capture (gdigrab) does not.

The script needs an ffmpeg build that has the gfxcapture filter (ffmpeg 8.0 or later).
It searches, in this order: -FfmpegPath, the portable copy under
Build/_AgentValidation/00000000-000000-shared/tools/ffmpeg, then ffmpeg on PATH. If no
build has gfxcapture, it stops and names Tools/Dependencies/Get-PortableFfmpeg.ps1.

.PARAMETER Window
The window to record. 'Editor' is the XREngine editor window. 'Monado' is the Monado
compositor preview window. Any other value is an ECMAScript regular expression that
matches the window title.

.PARAMETER SessionName
The name of an isolated MCP editor session (Tools/Manage-McpEditorSession.ps1). With
-Window Editor, the script records the editor process of that session. Without it, the
script records the first XREngine.Editor window.

.PARAMETER ProcessId
Records the main window of this process. This overrides -Window and -SessionName.

.PARAMETER Seconds
The recording duration in seconds.

.PARAMETER Fps
The output frame rate. The capture is limited to this rate.

.PARAMETER OutputPath
The MP4 file to write. The default is a time-stamped file in the mcp-captures folder of
the newest task run under Build/_AgentValidation.

.PARAMETER ShowCursor
Includes the mouse cursor in the video.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Record-WindowVideo.ps1 -Window Monado -Seconds 20

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Record-WindowVideo.ps1 -Window Editor -SessionName vrab -Seconds 30 -Fps 60
#>
param(
    [string]$Window = 'Editor',
    [string]$SessionName = '',
    [int]$ProcessId = 0,
    [ValidateRange(1, 3600)][int]$Seconds = 20,
    [ValidateRange(1, 240)][int]$Fps = 60,
    [string]$OutputPath = '',
    [string]$FfmpegPath = '',
    [switch]$ShowCursor
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sharedRoot = Join-Path $repoRoot 'Build\_AgentValidation\00000000-000000-shared'

function Test-GfxCapture([string]$exe)
{
    if (-not $exe -or -not (Test-Path $exe))
    {
        return $false
    }
    $filters = & $exe -hide_banner -filters 2>$null
    return [bool]($filters | Select-String -SimpleMatch 'gfxcapture')
}

function Resolve-Ffmpeg
{
    $candidates = @()
    if ($FfmpegPath)
    {
        $candidates += $FfmpegPath
    }
    $portable = Join-Path $sharedRoot 'tools\ffmpeg'
    if (Test-Path $portable)
    {
        $candidates += Get-ChildItem -Path $portable -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | ForEach-Object FullName
    }
    $onPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($onPath)
    {
        $candidates += $onPath.Source
    }

    foreach ($candidate in $candidates)
    {
        if (Test-GfxCapture $candidate)
        {
            return $candidate
        }
    }

    throw "No ffmpeg with the gfxcapture filter was found. Ask the user before you download one, then run Tools/Dependencies/Get-PortableFfmpeg.ps1, or pass -FfmpegPath."
}

function Resolve-SessionProcessId([string]$name)
{
    $sessions = Join-Path $sharedRoot 'mcp-sessions'
    $sessionFile = Get-ChildItem -Path $sessions -Directory -Filter "*-$name" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'session.json' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $sessionFile)
    {
        throw "No MCP editor session named '$name' was found under $sessions."
    }
    $session = Get-Content $sessionFile -Raw | ConvertFrom-Json
    if (-not $session.processId)
    {
        throw "The session file '$sessionFile' has no processId."
    }
    return [int]$session.processId
}

function Resolve-CaptureSource
{
    if ($ProcessId -gt 0)
    {
        $process = Get-Process -Id $ProcessId
        return @{ Option = "hwnd=$($process.MainWindowHandle.ToInt64())"; Label = "$($process.ProcessName) '$($process.MainWindowTitle)'" }
    }

    switch -Regex ($Window)
    {
        '^(?i)editor$'
        {
            $process = if ($SessionName) { Get-Process -Id (Resolve-SessionProcessId $SessionName) }
                else { Get-Process -Name 'XREngine.Editor' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
            if (-not $process -or $process.MainWindowHandle -eq 0)
            {
                throw 'No XREngine.Editor window was found.'
            }
            return @{ Option = "hwnd=$($process.MainWindowHandle.ToInt64())"; Label = "XREngine.Editor '$($process.MainWindowTitle)'" }
        }
        '^(?i)monado$'
        {
            return @{ Option = 'window_title=^Monado'; Label = 'Monado preview window' }
        }
        default
        {
            return @{ Option = "window_title=$Window"; Label = "window title matching '$Window'" }
        }
    }
}

$ffmpeg = Resolve-Ffmpeg
$source = Resolve-CaptureSource

if (-not $OutputPath)
{
    # Videos are task evidence, so they go to the newest task run, not the shared folder.
    $validationRoot = Join-Path $repoRoot 'Build\_AgentValidation'
    $taskRun = Get-ChildItem -Path $validationRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}-\d{6}-' -and $_.Name -notlike '00000000-000000-*' } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $taskRun)
    {
        throw 'No task run exists under Build/_AgentValidation. Create one (Tools/Limit-AgentValidation.ps1 -ReserveTaskRun) or pass -OutputPath.'
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $safeName = ($Window -replace '[^A-Za-z0-9_-]', '_')
    $OutputPath = Join-Path $taskRun.FullName "mcp-captures\$stamp-$safeName.mp4"
}
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory)
{
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$cursor = if ($ShowCursor) { 1 } else { 0 }
# gfxcapture produces GPU frames. hwdownload moves them to system memory for the
# encoder, and the scale filter makes both dimensions even, which yuv420p needs.
$graph = "gfxcapture=$($source.Option):max_framerate=$($Fps):capture_cursor=$($cursor),hwdownload,format=bgra,scale=trunc(iw/2)*2:trunc(ih/2)*2"

"Recording $($source.Label) for $Seconds s at $Fps fps to $OutputPath"
& $ffmpeg -hide_banner -loglevel error -y -f lavfi -i $graph -t $Seconds -r $Fps `
    -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p $OutputPath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $OutputPath))
{
    throw "ffmpeg failed with exit code $LASTEXITCODE."
}

# Report how many frames changed, so a static or frozen capture is easy to see.
$ffprobe = Join-Path (Split-Path -Parent $ffmpeg) 'ffprobe.exe'
$frames = if (Test-Path $ffprobe) { (& $ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 $OutputPath) } else { '?' }
$changed = (& $ffmpeg -v error -i $OutputPath -vf mpdecimate -f framemd5 - 2>$null | Where-Object { $_ -notmatch '^#' }).Count
"Wrote $OutputPath ($frames frames, $changed changed frames)"
