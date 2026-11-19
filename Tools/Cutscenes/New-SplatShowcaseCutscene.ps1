<#
.SYNOPSIS
Builds a five-shot avatar showcase cutscene in an isolated editor session over MCP, saves it as a world, and records it.

.DESCRIPTION
This script is a sample tool, not an engine feature. All values of the video are parameters or data in this script.
The script does these steps:

1. Starts an isolated MCP editor session and loads the authoring project.
2. Creates a new world with a two-light stage, imports the Unity avatar prefab and its walk clip, and instantiates the
   avatar. Without -TrianglesOnly, it adds a GaussianAvatarSplatComponent to the avatar root.
3. Reads the avatar bounds with get_node_bounds and computes five orbit camera paths around the avatar.
4. Creates one camera for each shot, a screen-space canvas with a title and a subtitle, and a director.
5. Writes the camera cut, camera transform, field of view, animation clip, and overlay tracks with the cutscene
   authoring tools, then saves the cutscene asset and the world.
6. Stops the session and records the world with --record-cutscene.

The shots move across the walking avatar from the feet up to the face, and end on a full-body push-in shot that reveals
the title and the subtitle on the left side.

.PARAMETER AllowPartialImport
Keeps importing the prefab when the Unity project is missing textures or materials. The missing parts use fallback
materials, so they do not match the Unity look.

.EXAMPLE
powershell -NoProfile -File Tools/Cutscenes/New-SplatShowcaseCutscene.ps1 `
  -ProjectPath "<project>/<name>.xrproj" -UnityPrefab "<unity-project>/Assets/<path>/Avatar.prefab" `
  -AnimationFile "<unity-project>/Assets/<path>/Walk.anim" -OutputDir "<output>/showcase" -FfmpegPath "<ffmpeg-path>"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [Parameter(Mandatory = $true)][string]$UnityPrefab,
    [Parameter(Mandatory = $true)][string]$AnimationFile,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$Title = 'XRENGINE',
    [string]$Subtitle = 'GAUSSIAN SPLAT AVATAR  //  CUTSCENE PLAYBACK',
    [string]$WorldName = 'SplatShowcase',
    [string]$AssetFolder = 'Cutscenes/SplatShowcase',
    [ValidateRange(10000, 8000000)][int]$SplatCount = 2400000,
    [ValidateRange(2.0, 60.0)][double]$DurationSeconds = 8.8,
    [ValidateRange(10, 120)][int]$Fps = 60,
    [double]$YawOffsetDegrees = 0.0,
    [double]$ClipLengthSeconds = 0.0,
    [switch]$TrianglesOnly,
    [ValidateSet('vulkan', 'opengl')][string]$Backend = 'vulkan',
    [ValidateSet('nvenc', 'x264')][string]$VideoEncoder = 'nvenc',
    [string]$FfmpegPath = 'ffmpeg',
    [long]$OptimizedMaxBytes = 0,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$SessionName = 'splat-showcase',
    [switch]$SkipRecording,
    [switch]$NoSessionBuild,
    [switch]$AllowPartialImport,
    [hashtable]$SessionEnvironment = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$inv = [System.Globalization.CultureInfo]::InvariantCulture

# ---------------------------------------------------------------------------------------------------------------------
# Shot data. Fractions are of the cutscene duration. Distances and focus heights are factors of the avatar height.
# Yaw is relative to the front of the avatar. The clip fractions select the part of the walk clip that each shot shows,
# so a jump cut also jumps in the walk cycle.
# ---------------------------------------------------------------------------------------------------------------------
$Shots = @(
    @{ Notes = 'Low angle, feet and legs';                Start = 0.000; End = 0.142; Yaw = @(-28, -14);   Pitch = @(-5.5, -2.0); Focus = @(0.22, 0.31); Bias = 0.22; Distance = @(0.78, 0.70); Fov = @(30, 28); Lead = @(0.0, 0.0);  Clip = @(0.02, 0.41) },
    @{ Notes = 'Rear three-quarter, hips';                Start = 0.142; End = 0.290; Yaw = @(-122, -98);  Pitch = @(5.0, 2.5);   Focus = @(0.44, 0.50); Bias = 0.05; Distance = @(0.88, 0.80); Fov = @(32, 30); Lead = @(0.0, 0.0);  Clip = @(0.41, 0.82) },
    @{ Notes = 'Front three-quarter, torso and arms';     Start = 0.290; End = 0.438; Yaw = @(40, 20);     Pitch = @(4.0, 2.0);   Focus = @(0.56, 0.66); Bias = 0.22; Distance = @(0.72, 0.64); Fov = @(29, 27); Lead = @(0.0, 0.0);  Clip = @(0.08, 0.49) },
    @{ Notes = 'Close-up, head';                          Start = 0.438; End = 0.580; Yaw = @(-22, -8);    Pitch = @(4.5, 2.0);   Focus = @(0.87, 0.89); Bias = 0.22; Distance = @(0.64, 0.56); Fov = @(27, 25); Lead = @(0.0, 0.0);  Clip = @(0.49, 0.88) },
    @{ Notes = 'Full body push-in with the title reveal'; Start = 0.580; End = 1.000; Yaw = @(32, 15);     Pitch = @(6.5, 3.0);   Focus = @(0.51, 0.52); Bias = 0.16; Distance = @(2.02, 1.72); Fov = @(38, 35); Lead = @(0.30, 0.34); Clip = @(0.02, 0.96); HoldFraction = 0.78 }
)

$TitleStyle = @{ FontSize = 72.0; Position = @(96.0, 300.0); Size = @(920.0, 110.0); Fill = @(0.97, 0.98, 1.0); Outline = @(0.04, 0.09, 0.18); SlideFrom = @(28.0, 300.0); ScaleFrom = 0.92; Opacity = 1.0; OutlineOpacity = 0.95; OutlinePeak = 4.2; OutlineSettled = 2.6 }
$SubtitleStyle = @{ FontSize = 28.0; Position = @(100.0, 232.0); Size = @(920.0, 64.0); Fill = @(0.66, 0.86, 1.0); Outline = @(0.03, 0.06, 0.14); SlideFrom = @(48.0, 232.0); ScaleFrom = 0.95; Opacity = 1.0; OutlineOpacity = 0.90; OutlinePeak = 3.0; OutlineSettled = 1.8 }
$StageLights = @(
    @{ Name = 'KeyLight';  Direction = @(-0.4, -1.0, -0.3); Intensity = 3.0 },
    @{ Name = 'FillLight'; Direction = @(0.5, -0.3, 0.6);   Intensity = 1.0 }
)
$AmbientIntensity = 0.3
$CameraSampleRate = 60.0

# ---------------------------------------------------------------------------------------------------------------------
# Math helpers. Quaternions are @(x, y, z, w) and follow System.Numerics conventions.
# ---------------------------------------------------------------------------------------------------------------------
function Lerp([double]$a, [double]$b, [double]$t) { $c = [Math]::Min(1.0, [Math]::Max(0.0, $t)); return $a + (($b - $a) * $c) }
function SmoothStep([double]$t) { $x = [Math]::Min(1.0, [Math]::Max(0.0, $t)); return $x * $x * (3.0 - (2.0 * $x)) }
function CubicEaseOut([double]$t) { $x = 1.0 - [Math]::Min(1.0, [Math]::Max(0.0, $t)); return 1.0 - ($x * $x * $x) }
function Normalize([double[]]$v) { $l = [Math]::Sqrt(($v[0] * $v[0]) + ($v[1] * $v[1]) + ($v[2] * $v[2])); return @(($v[0] / $l), ($v[1] / $l), ($v[2] / $l)) }
function Cross([double[]]$a, [double[]]$b) { return @((($a[1] * $b[2]) - ($a[2] * $b[1])), (($a[2] * $b[0]) - ($a[0] * $b[2])), (($a[0] * $b[1]) - ($a[1] * $b[0]))) }

function QuatFromYawPitchRoll([double]$yaw, [double]$pitch, [double]$roll) {
    $sr = [Math]::Sin($roll * 0.5); $cr = [Math]::Cos($roll * 0.5)
    $sp = [Math]::Sin($pitch * 0.5); $cp = [Math]::Cos($pitch * 0.5)
    $sy = [Math]::Sin($yaw * 0.5); $cy = [Math]::Cos($yaw * 0.5)
    return @(
        (($cy * $sp * $cr) + ($sy * $cp * $sr)),
        (($sy * $cp * $cr) - ($cy * $sp * $sr)),
        (($cy * $cp * $sr) - ($sy * $sp * $cr)),
        (($cy * $cp * $cr) + ($sy * $sp * $sr)))
}

function RotateVector([double[]]$v, [double[]]$q) {
    $x2 = $q[0] + $q[0]; $y2 = $q[1] + $q[1]; $z2 = $q[2] + $q[2]
    $wx2 = $q[3] * $x2; $wy2 = $q[3] * $y2; $wz2 = $q[3] * $z2
    $xx2 = $q[0] * $x2; $xy2 = $q[0] * $y2; $xz2 = $q[0] * $z2
    $yy2 = $q[1] * $y2; $yz2 = $q[1] * $z2; $zz2 = $q[2] * $z2
    return @(
        (($v[0] * (1.0 - $yy2 - $zz2)) + ($v[1] * ($xy2 - $wz2)) + ($v[2] * ($xz2 + $wy2))),
        (($v[0] * ($xy2 + $wz2)) + ($v[1] * (1.0 - $xx2 - $zz2)) + ($v[2] * ($yz2 - $wx2))),
        (($v[0] * ($xz2 - $wy2)) + ($v[1] * ($yz2 + $wx2)) + ($v[2] * (1.0 - $xx2 - $yy2))))
}

# The rotation of Matrix4x4.CreateWorld(0, forward, up): local -Z points along forward.
function LookRotation([double[]]$forward, [double[]]$up) {
    $f = Normalize $forward
    $dot = [Math]::Abs(($f[0] * $up[0]) + ($f[1] * $up[1]) + ($f[2] * $up[2]))
    if ($dot -gt 0.99) { $up = @(0.0, 0.0, 1.0) }
    $z = @(-$f[0], -$f[1], -$f[2])
    $x = Normalize (Cross $up $z)
    $y = Cross $z $x
    $m11 = $x[0]; $m12 = $x[1]; $m13 = $x[2]
    $m21 = $y[0]; $m22 = $y[1]; $m23 = $y[2]
    $m31 = $z[0]; $m32 = $z[1]; $m33 = $z[2]
    $trace = $m11 + $m22 + $m33
    if ($trace -gt 0.0) {
        $s = [Math]::Sqrt($trace + 1.0); $w = $s * 0.5; $s = 0.5 / $s
        return @((($m23 - $m32) * $s), (($m31 - $m13) * $s), (($m12 - $m21) * $s), $w)
    }
    if ($m11 -ge $m22 -and $m11 -ge $m33) {
        $s = [Math]::Sqrt(1.0 + $m11 - $m22 - $m33); $inv2 = 0.5 / $s
        return @((0.5 * $s), (($m12 + $m21) * $inv2), (($m13 + $m31) * $inv2), (($m23 - $m32) * $inv2))
    }
    if ($m22 -gt $m33) {
        $s = [Math]::Sqrt(1.0 + $m22 - $m11 - $m33); $inv2 = 0.5 / $s
        return @((($m21 + $m12) * $inv2), (0.5 * $s), (($m32 + $m23) * $inv2), (($m31 - $m13) * $inv2))
    }
    $s = [Math]::Sqrt(1.0 + $m33 - $m11 - $m22); $inv2 = 0.5 / $s
    return @((($m31 + $m13) * $inv2), (($m32 + $m23) * $inv2), (0.5 * $s), (($m12 - $m21) * $inv2))
}

function Deg([double]$degrees) { return $degrees * [Math]::PI / 180.0 }

# ---------------------------------------------------------------------------------------------------------------------
# MCP helpers.
# ---------------------------------------------------------------------------------------------------------------------
function Invoke-Tool([string]$Name, [hashtable]$Arguments) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Name)
    $raw = & (Join-Path $repoRoot 'Tools\Invoke-Mcp.ps1') -Session $SessionName -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments } -TimeoutSec 1800 -OutputDepth 30
    $response = ($raw -join "`n") | ConvertFrom-Json
    if ($null -ne $response.PSObject.Properties['error'] -and $null -ne $response.error) {
        throw "MCP tool '$Name' failed: $($response.error.message)"
    }
    $result = $response.result
    if ($result.isError) {
        throw "MCP tool '$Name' failed: $($result.content[0].text)"
    }
    return $result.structuredContent
}

function New-Node([string]$Name, [string]$ParentId) {
    $arguments = @{ name = $Name }
    if ($ParentId) { $arguments.parent_id = $ParentId }
    return [string](Invoke-Tool 'create_scene_node' $arguments).id
}

function Add-Component([string]$NodeId, [string]$Type, [hashtable]$Properties) {
    $component = Invoke-Tool 'add_component_to_node' @{ node_id = $NodeId; component_type = $Type }
    if ($null -ne $Properties -and $Properties.Count -gt 0) {
        [void](Invoke-Tool 'set_component_properties' @{ node_id = $NodeId; component_type = $Type; properties = $Properties; stop_on_error = $true })
    }
    return [string]$component.componentId
}

function Set-ObjectProperty([string]$ObjectId, [string]$Path, [string]$Property, $Value) {
    $arguments = @{ object_id = $ObjectId; property_name = $Property; value = $Value }
    if ($Path) { $arguments.object_path = $Path }
    [void](Invoke-Tool 'set_object_property' $arguments)
}

function Color3([double[]]$c) { return @{ R = $c[0]; G = $c[1]; B = $c[2] } }
function Color4([double[]]$c, [double]$a) { return @{ R = $c[0]; G = $c[1]; B = $c[2]; A = $a } }
function Curve([double[]]$Times, [double[]]$Values) { return @{ times = $Times; values = $Values; interpolation = 'Linear' } }

# ---------------------------------------------------------------------------------------------------------------------
# Build the world.
# ---------------------------------------------------------------------------------------------------------------------
$sessionScript = Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1'
Write-Host "Starting the editor session '$SessionName'."
# MCP tools need an active world, so the session starts with the unit testing world and create_world replaces it.
# Imported assets go to the Assets folder of the project, not to the repository.
$projectAssets = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($ProjectPath))) 'Assets'
$environment = @{ XRE_GAME_ASSETS_PATH = $projectAssets }
foreach ($key in $SessionEnvironment.Keys) { $environment[$key] = [string]$SessionEnvironment[$key] }
$startArguments = @{ Name = $SessionName; Configuration = $Configuration; SessionEnvironment = $environment }
if ($NoSessionBuild) { $startArguments.NoBuild = $true }
& $sessionScript Start @startArguments | Out-Host
$worldPath = $null
try {
    # The MCP server answers before the startup world exists. Tools that need a world fail until it loads.
    $worldWait = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try { [void](Invoke-Tool 'list_scenes' @{}); break }
        catch {
            if ($worldWait.Elapsed.TotalSeconds -gt 300) { throw }
            Start-Sleep -Seconds 2
        }
    }

    [void](Invoke-Tool 'load_game_project' @{ project_path = [System.IO.Path]::GetFullPath($ProjectPath) })
    $world = Invoke-Tool 'create_world' @{ name = $WorldName }
    Set-ObjectProperty ([string]$world.settingsId) $null 'AmbientLightColor' @{ R = 1.0; G = 1.0; B = 1.0 }
    Set-ObjectProperty ([string]$world.settingsId) $null 'AmbientLightIntensity' $AmbientIntensity

    # The stage faces the avatar front, which is +Z after the 180 degree turn.
    $stageId = New-Node 'Stage' $null
    [void](Invoke-Tool 'set_node_transform' @{ node_id = $stageId; yaw = 180.0 })
    foreach ($light in $StageLights) {
        $lightId = New-Node $light.Name $stageId
        $rotation = LookRotation (Normalize $light.Direction) @(0.0, 1.0, 0.0)
        Set-ObjectProperty $lightId 'Transform' 'Rotation' @{ X = $rotation[0]; Y = $rotation[1]; Z = $rotation[2]; W = $rotation[3] }
        [void](Add-Component $lightId 'DirectionalLightComponent' @{ Color = (Color3 @(1.0, 1.0, 1.0)); DiffuseIntensity = $light.Intensity; CastsShadows = $false })
    }

    $prefabImport = Invoke-Tool 'import_third_party_asset' @{ source_path = [System.IO.Path]::GetFullPath($UnityPrefab); dest_folder = $AssetFolder; overwrite = $true; allow_partial_import = [bool]$AllowPartialImport }
    $clipImport = Invoke-Tool 'import_third_party_asset' @{ source_path = [System.IO.Path]::GetFullPath($AnimationFile); dest_folder = $AssetFolder; overwrite = $true }
    $avatar = Invoke-Tool 'instantiate_prefab' @{ prefab_path = [string]$prefabImport.path }
    $avatarId = [string]$avatar.instanceId
    if (-not $TrianglesOnly) {
        [void](Add-Component $avatarId 'GaussianAvatarSplatComponent' @{ TargetSplatCount = $SplatCount })
    }

    if ($ClipLengthSeconds -le 0.0) {
        $clipAsset = Invoke-Tool 'read_game_asset' @{ path = [string]$clipImport.path }
        $match = [regex]::Match(($clipAsset | ConvertTo-Json -Depth 5), 'LengthInSeconds\\?"?:\s*([0-9.]+)')
        if (-not $match.Success) { throw 'The clip length is not in the imported clip. Pass -ClipLengthSeconds.' }
        $ClipLengthSeconds = [double]::Parse($match.Groups[1].Value, $inv)
    }

    $bounds = Invoke-Tool 'get_node_bounds' @{ node_id = $avatarId }
    $min = @([double]$bounds.min[0], [double]$bounds.min[1], [double]$bounds.min[2])
    $max = @([double]$bounds.max[0], [double]$bounds.max[1], [double]$bounds.max[2])
    $height = [Math]::Max($max[1] - $min[1], 0.4)
    $centerX = ($min[0] + $max[0]) * 0.5
    $centerZ = ($min[2] + $max[2]) * 0.5
    $nearZ = [Math]::Min(0.15, [Math]::Max(0.015, $height * 0.02))
    $farZ = [Math]::Max($height * 25.0, 50.0)
    $frontYaw = 180.0 + $YawOffsetDegrees
    $avatarForward = @([Math]::Sin((Deg $frontYaw)), 0.0, [Math]::Cos((Deg $frontYaw)))
    Write-Host ("Avatar height {0:F2} m, clip length {1:F2} s." -f $height, $ClipLengthSeconds)

    # Cameras, overlays, and the director.
    $cameraGroupId = New-Node 'Cameras' $null
    $cameraIds = @()
    for ($i = 0; $i -lt $Shots.Count; $i++) {
        $cameraId = New-Node ("ShotCamera{0}" -f $i) $cameraGroupId
        $componentId = Add-Component $cameraId 'CameraComponent' @{}
        Set-ObjectProperty $componentId 'CameraParameters' 'NearZ' $nearZ
        Set-ObjectProperty $componentId 'CameraParameters' 'FarZ' $farZ
        $cameraIds += $cameraId
    }

    $canvasId = New-Node 'Overlay' $null
    [void](Add-Component $canvasId 'UICanvasComponent' @{})
    Set-ObjectProperty $canvasId 'Transform' 'DrawSpace' 'Screen'
    Set-ObjectProperty $canvasId 'Transform' 'Padding' @{ X = 0.0; Y = 0.0; Z = 0.0; W = 0.0 }

    function New-TextNode([string]$Name, [string]$Text, [hashtable]$Style) {
        $nodeId = New-Node $Name $canvasId
        [void](Add-Component $nodeId 'UITextComponent' @{
            FontSize = $Style.FontSize; HorizontalAlignment = 'Left'; VerticalAlignment = 'Center'; WrapMode = 'None'
            Color = (Color4 $Style.Fill 0.0); OutlineColor = (Color4 $Style.Outline 0.0); OutlineThickness = 0.0; Text = $Text })
        Set-ObjectProperty $nodeId 'Transform' 'MinAnchor' @{ X = 0.0; Y = 0.0 }
        Set-ObjectProperty $nodeId 'Transform' 'MaxAnchor' @{ X = 0.0; Y = 0.0 }
        Set-ObjectProperty $nodeId 'Transform' 'NormalizedPivot' @{ X = 0.0; Y = 0.0 }
        Set-ObjectProperty $nodeId 'Transform' 'Width' $Style.Size[0]
        Set-ObjectProperty $nodeId 'Transform' 'Height' $Style.Size[1]
        Set-ObjectProperty $nodeId 'Transform' 'Translation' @{ X = $Style.Position[0]; Y = $Style.Position[1] }
        return $nodeId
    }
    $titleId = New-TextNode 'Title' $Title $TitleStyle
    $subtitleId = New-TextNode 'Subtitle' $Subtitle $SubtitleStyle

    $directorId = New-Node 'Director' $null
    [void](Invoke-Tool 'cutscene_create_asset' @{ node_id = $directorId; duration_seconds = $DurationSeconds; name = $WorldName })

    # Camera cuts, camera paths, and walk clip windows.
    $cutClips = @()
    $animationClips = @()
    for ($i = 0; $i -lt $Shots.Count; $i++) {
        $shot = $Shots[$i]
        $isLast = ($i -eq $Shots.Count - 1)
        $start = if ($i -eq 0) { 0.0 } else { $DurationSeconds * $shot.Start }
        $end = if ($isLast) { $DurationSeconds } else { $DurationSeconds * $shot.End }
        $duration = $end - $start
        $extrapolation = if ($isLast) { 'Hold' } else { 'None' }
        $cutClips += @{ start = $start; duration = $duration; camera_node_id = $cameraIds[$i]; extrapolation = $extrapolation }

        # Clips must not overlap, so each clip but the last ends one microsecond before the next shot.
        $clipDuration = if ($isLast) { $duration } else { $duration - 0.000001 }
        $clipOffset = $ClipLengthSeconds * $shot.Clip[0]
        $speed = ($ClipLengthSeconds * ($shot.Clip[1] - $shot.Clip[0])) / $clipDuration
        $animationClips += @{ start = $start; duration = $clipDuration; clip_offset = $clipOffset; speed = $speed; extrapolation = $extrapolation; animation_clip_path = [string]$clipImport.path }

        $samples = [Math]::Max(32, [int][Math]::Ceiling($duration * $CameraSampleRate) + 1)
        $times = New-Object double[] $samples
        $positions = New-Object double[] ($samples * 3)
        $rotations = New-Object double[] ($samples * 4)
        $fovs = New-Object double[] $samples
        $biasOffset = @(($avatarForward[0] * $height * $shot.Bias), 0.0, ($avatarForward[2] * $height * $shot.Bias))
        for ($k = 0; $k -lt $samples; $k++) {
            $u = $k / ($samples - 1.0)
            $times[$k] = $u * $duration
            $holdFraction = if ($shot.ContainsKey('HoldFraction')) { $shot.HoldFraction } else { 1.0 }
            $eased = SmoothStep ([Math]::Min(1.0, $u / $holdFraction))
            $yaw = Deg ($frontYaw + (Lerp $shot.Yaw[0] $shot.Yaw[1] $eased))
            $pitch = Deg (Lerp $shot.Pitch[0] $shot.Pitch[1] $eased)
            $distance = $height * (Lerp $shot.Distance[0] $shot.Distance[1] $eased)
            $focusY = $min[1] + ($height * (Lerp $shot.Focus[0] $shot.Focus[1] $eased))
            $lead = $height * (Lerp $shot.Lead[0] $shot.Lead[1] $eased)
            $orbit = QuatFromYawPitchRoll $yaw (-$pitch) 0.0
            $cameraForward = RotateVector @(0.0, 0.0, -1.0) $orbit
            $cameraRight = Normalize (Cross $cameraForward @(0.0, 1.0, 0.0))
            $focus = @(($centerX + $biasOffset[0] - ($cameraRight[0] * $lead)), ($focusY - ($cameraRight[1] * $lead)), ($centerZ + $biasOffset[2] - ($cameraRight[2] * $lead)))
            $offset = RotateVector @(0.0, 0.0, $distance) $orbit
            $position = @(($focus[0] + $offset[0]), ($focus[1] + $offset[1]), ($focus[2] + $offset[2]))
            $look = Normalize @(($focus[0] - $position[0]), ($focus[1] - $position[1]), ($focus[2] - $position[2]))
            $rotation = LookRotation $look @(0.0, 1.0, 0.0)
            $positions[(3 * $k)] = $position[0]; $positions[(3 * $k) + 1] = $position[1]; $positions[(3 * $k) + 2] = $position[2]
            $rotations[(4 * $k)] = $rotation[0]; $rotations[(4 * $k) + 1] = $rotation[1]; $rotations[(4 * $k) + 2] = $rotation[2]; $rotations[(4 * $k) + 3] = $rotation[3]
            $fovs[$k] = Lerp $shot.Fov[0] $shot.Fov[1] $eased
        }

        [void](Invoke-Tool 'cutscene_add_track' @{
            director_node_id = $directorId; track_type = 'transform'; target_node_id = $cameraIds[$i]; name = ("Shot{0} {1}" -f $i, $shot.Notes); when_finished = 'KeepState'
            clips = @(@{ start = $start; duration = $duration; extrapolation = 'Hold'; position = (Curve $times $positions); rotation = (Curve $times $rotations) }) })
        [void](Invoke-Tool 'cutscene_add_track' @{
            director_node_id = $directorId; track_type = 'camera_property'; target_node_id = $cameraIds[$i]; name = ("Shot{0} field of view" -f $i); when_finished = 'KeepState'
            clips = @(@{ start = $start; duration = $duration; extrapolation = 'Hold'; field_of_view = (Curve $times $fovs) }) })
    }

    [void](Invoke-Tool 'cutscene_add_track' @{ director_node_id = $directorId; track_type = 'camera_cut'; target_node_id = $cameraIds[0]; name = 'Camera cuts'; when_finished = 'KeepState'; clips = $cutClips })
    [void](Invoke-Tool 'cutscene_add_track' @{ director_node_id = $directorId; track_type = 'animation_clip'; target_node_id = $avatarId; name = 'Walk'; when_finished = 'KeepState'; clips = $animationClips })

    # Overlay reveals: hidden during the close shots, revealed shortly after the last cut, and held to the end.
    $lastCut = $DurationSeconds * $Shots[$Shots.Count - 1].Start
    $titleStart = $lastCut + 0.04
    $titleEnd = [Math]::Min($lastCut + 0.52, $DurationSeconds - 3.15)
    if ($titleEnd -le $titleStart + 0.2) {
        $titleStart = [Math]::Max($lastCut + 0.02, $DurationSeconds - 3.65)
        $maxReveal = [Math]::Max(0.08, ($DurationSeconds - $titleStart) * 0.35)
        $titleEnd = [Math]::Min($DurationSeconds - 0.04, [Math]::Max($titleStart + [Math]::Min(0.42, $maxReveal), $DurationSeconds - 3.15))
    }
    $subtitleStart = [Math]::Min($titleStart + 0.14, [Math]::Max($titleStart + 0.02, $DurationSeconds - 0.12))
    $subtitleEnd = [Math]::Min($titleEnd + 0.18, [Math]::Max($subtitleStart + 0.04, $DurationSeconds - 0.02))

    function Add-OverlayTracks([string]$NodeId, [string]$Label, [hashtable]$Style, [double]$RevealStart, [double]$RevealEnd) {
        $slideTimes = @(0.0); $slidePositions = @($Style.SlideFrom[0], $Style.SlideFrom[1], 0.0); $slideScales = @($Style.ScaleFrom, $Style.ScaleFrom, 1.0)
        for ($k = 0; $k -lt 32; $k++) {
            $u = $k / 31.0; $e = CubicEaseOut $u
            $slideTimes += (Lerp $RevealStart $RevealEnd $u)
            $slidePositions += @((Lerp $Style.SlideFrom[0] $Style.Position[0] $e), (Lerp $Style.SlideFrom[1] $Style.Position[1] $e), 0.0)
            $s = Lerp $Style.ScaleFrom 1.0 $e
            $slideScales += @($s, $s, 1.0)
        }
        $slideTimes += $DurationSeconds; $slidePositions += @($Style.Position[0], $Style.Position[1], 0.0); $slideScales += @(1.0, 1.0, 1.0)
        [void](Invoke-Tool 'cutscene_add_track' @{
            director_node_id = $directorId; track_type = 'transform'; target_node_id = $NodeId; name = "$Label slide"; when_finished = 'KeepState'
            clips = @(@{ start = 0.0; duration = $DurationSeconds; extrapolation = 'Hold'; position = (Curve $slideTimes $slidePositions)
                         rotation = (Curve @(0.0, $DurationSeconds) @(0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)); scale = (Curve $slideTimes $slideScales) }) })

        foreach ($fade in @(@{ Member = 'Opacity'; Target = $Style.Opacity }, @{ Member = 'OutlineOpacity'; Target = $Style.OutlineOpacity })) {
            $fadeTimes = @(0.0); $fadeValues = @(0.0)
            for ($k = 0; $k -lt 24; $k++) {
                $u = $k / 23.0
                $fadeTimes += (Lerp $RevealStart $RevealEnd $u)
                $fadeValues += ($fade.Target * (SmoothStep $u))
            }
            $fadeTimes += $DurationSeconds; $fadeValues += $fade.Target
            [void](Invoke-Tool 'cutscene_add_track' @{
                director_node_id = $directorId; track_type = 'property'; target_node_id = $NodeId; name = "$Label $($fade.Member)"; when_finished = 'KeepState'
                component_type = 'UITextComponent'; member_path = $fade.Member
                clips = @(@{ start = 0.0; duration = $DurationSeconds; extrapolation = 'Hold'; float_curve = (Curve $fadeTimes $fadeValues) }) })
        }

        $peak = Lerp $RevealStart $RevealEnd 0.55
        [void](Invoke-Tool 'cutscene_add_track' @{
            director_node_id = $directorId; track_type = 'property'; target_node_id = $NodeId; name = "$Label outline thickness"; when_finished = 'KeepState'
            component_type = 'UITextComponent'; member_path = 'OutlineThickness'
            clips = @(@{ start = 0.0; duration = $DurationSeconds; extrapolation = 'Hold'
                         float_curve = (Curve @(0.0, $RevealStart, $peak, ($RevealEnd + 0.15), $DurationSeconds) @(0.0, 0.0, $Style.OutlinePeak, $Style.OutlineSettled, $Style.OutlineSettled)) }) })
    }
    Add-OverlayTracks $titleId 'Title' $TitleStyle $titleStart $titleEnd
    Add-OverlayTracks $subtitleId 'Subtitle' $SubtitleStyle $subtitleStart $subtitleEnd

    [void](Invoke-Tool 'cutscene_save_asset' @{ director_node_id = $directorId; asset_folder = $AssetFolder })
    $saved = Invoke-Tool 'save_world' @{}
    $worldPath = [string]$saved.filePath
    Write-Host "Saved the world to '$worldPath'."
}
finally {
    & $sessionScript Stop -Name $SessionName | Out-Host
}

if ($SkipRecording) {
    return
}

# ---------------------------------------------------------------------------------------------------------------------
# Record the saved world.
# ---------------------------------------------------------------------------------------------------------------------
$recordArguments = @(
    'run', '--no-build', '-c', $Configuration, '--project', (Join-Path $repoRoot 'XREngine.Editor\XREngine.Editor.csproj'), '--',
    '--record-cutscene',
    '--project', [System.IO.Path]::GetFullPath($ProjectPath),
    '--world', $worldPath,
    '--director', 'Director',
    '--output-dir', [System.IO.Path]::GetFullPath($OutputDir),
    '--backend', $Backend,
    '--fps', $Fps.ToString($inv),
    '--video-encoder', $VideoEncoder,
    '--optimized-max-bytes', $OptimizedMaxBytes.ToString($inv),
    '--ffmpeg', $FfmpegPath)
& dotnet @recordArguments
if ($LASTEXITCODE -ne 0) {
    throw "The cutscene recording failed with exit code $LASTEXITCODE."
}
