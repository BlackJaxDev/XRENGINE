[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourceUrl = "https://github.com/BlackJaxDev/Monado.git",

    [Parameter()]
    [string]$Ref = "main",

    [Parameter()]
    [string]$SourceDir = "Build\Submodules\monado",

    [Parameter()]
    [string]$BuildDir = "Build\Submodules\monado\build",

    [Parameter()]
    [string]$InstallDir = "Build\Deps\Monado",

    [Parameter()]
    [string]$VcpkgDir,

    [Parameter()]
    [ValidateSet("Debug", "Release", "RelWithDebInfo")]
    [string]$Configuration = "Release",

    [Parameter()]
    [ValidateSet("AnyCPU", "x64")]
    [string]$EditorPlatform = "AnyCPU",

    [Parameter()]
    [string]$EditorConfiguration = "Debug",

    [Parameter()]
    [string]$VsVcVars64Bat,

    [Parameter()]
    [string[]]$ExtraCMakeArgs = @(),

    [Parameter()]
    [switch]$InstallPrerequisites,

    [Parameter()]
    [switch]$ForceClone,

    [Parameter()]
    [switch]$ForceVcpkg,

    [Parameter()]
    [switch]$NoFetch,

    [Parameter()]
    [switch]$Clean,

    [Parameter()]
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$installScript = Join-Path $PSScriptRoot "Install-Monado.ps1"
if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
    throw "Install-Monado.ps1 was not found: $installScript"
}

$installParameters = @{
    SourceUrl           = $SourceUrl
    Ref                 = $Ref
    SourceDir           = $SourceDir
    BuildDir            = $BuildDir
    InstallDir          = $InstallDir
    Configuration       = $Configuration
    EditorPlatform      = $EditorPlatform
    EditorConfiguration = $EditorConfiguration
    ExtraCMakeArgs      = $ExtraCMakeArgs
    BuildOnly           = $true
}

foreach ($optionalParameter in @("VcpkgDir", "VsVcVars64Bat")) {
    if ($PSBoundParameters.ContainsKey($optionalParameter)) {
        $installParameters[$optionalParameter] = $PSBoundParameters[$optionalParameter]
    }
}

foreach ($switchParameter in @("InstallPrerequisites", "ForceClone", "ForceVcpkg", "NoFetch", "Clean", "SkipBuild")) {
    if ($PSBoundParameters.ContainsKey($switchParameter)) {
        $installParameters[$switchParameter] = [bool]$PSBoundParameters[$switchParameter]
    }
}

& $installScript @installParameters
