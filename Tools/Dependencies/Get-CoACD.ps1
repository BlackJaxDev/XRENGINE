param(
    [ValidateSet("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Rid = "win-x64",
    [string]$Version = "1.0.7",
    [switch]$ForceDownload
)

$wheelMap = @{
    "win-x64"     = @{ Suffix = "win_amd64"; Binary = "lib_coacd.dll"; Runtime = "XRENGINE/runtimes/win-x64/native" }
    "linux-x64"   = @{ Suffix = "manylinux_2_17_x86_64.manylinux2014_x86_64"; Binary = "lib_coacd.so"; Runtime = "XRENGINE/runtimes/linux-x64/native" }
    "linux-arm64" = @{ Suffix = "manylinux_2_17_aarch64.manylinux2014_aarch64"; Binary = "lib_coacd.so"; Runtime = "XRENGINE/runtimes/linux-arm64/native" }
    "osx-x64"     = @{ Suffix = "macosx_11_0_x86_64"; Binary = "lib_coacd.dylib"; Runtime = "XRENGINE/runtimes/osx-x64/native" }
    "osx-arm64"   = @{ Suffix = "macosx_11_0_arm64"; Binary = "lib_coacd.dylib"; Runtime = "XRENGINE/runtimes/osx-arm64/native" }
}

if (-not $wheelMap.ContainsKey($Rid)) {
    throw "Unsupported runtime identifier: $Rid"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$downloadsDir = Join-Path $repoRoot "Build\Dependencies\CoACD"
$tempExtract = Join-Path $downloadsDir "extracted"
$info = $wheelMap[$Rid]
$wheelName = "coacd-$Version-cp39-abi3-$($info.Suffix).whl"
$downloadUrl = "https://github.com/SarahWeiii/CoACD/releases/download/$Version/$wheelName"
$wheelPath = Join-Path $downloadsDir $wheelName
$targetRuntimeDir = Join-Path $repoRoot $info.Runtime
$targetBinary = Join-Path $targetRuntimeDir $info.Binary

New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null
New-Item -ItemType Directory -Path $targetRuntimeDir -Force | Out-Null

$shouldDownload = $ForceDownload -or -not (Test-Path $wheelPath)
if ($shouldDownload) {
    Write-Host "Downloading CoACD $Version wheel for $Rid..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $wheelPath
}
else {
    Write-Host "Reusing existing wheel at $wheelPath"
}

if (Test-Path $tempExtract) {
    Remove-Item $tempExtract -Recurse -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($wheelPath, $tempExtract)

$extractedBinary = Join-Path (Join-Path $tempExtract "coacd") $info.Binary
if (-not (Test-Path $extractedBinary)) {
    throw "Unable to locate $($info.Binary) in extracted wheel."
}

Copy-Item $extractedBinary -Destination $targetBinary -Force
Write-Host "Copied $($info.Binary) to $targetRuntimeDir"

Remove-Item $tempExtract -Recurse -Force
Write-Host "Completed CoACD setup."
