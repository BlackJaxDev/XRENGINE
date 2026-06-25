[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $expanded))
}

function Resolve-RuntimeLibraryPath {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$LibraryPath
    )

    $expanded = [Environment]::ExpandEnvironmentVariables($LibraryPath)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    $manifestDirectory = Split-Path -Parent $ManifestPath
    return [System.IO.Path]::GetFullPath((Join-Path $manifestDirectory $expanded))
}

function Add-Candidate {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Candidates,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $Candidates.Add((Resolve-FullPath $Path))
}

$candidates = [System.Collections.Generic.List[string]]::new()

if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) {
    Add-Candidate -Candidates $candidates -Path $RuntimeJson
}
else {
    Add-Candidate -Candidates $candidates -Path $env:XR_RUNTIME_JSON
    Add-Candidate -Candidates $candidates -Path $env:MONADO_RUNTIME_JSON

    if (-not [string]::IsNullOrWhiteSpace($env:MONADO_INSTALL_DIR)) {
        Add-Candidate -Candidates $candidates -Path (Join-Path $env:MONADO_INSTALL_DIR "share\openxr\1\openxr_monado.json")
        Add-Candidate -Candidates $candidates -Path (Join-Path $env:MONADO_INSTALL_DIR "share\openxr\1\openxr_monado-dev.json")
        Add-Candidate -Candidates $candidates -Path (Join-Path $env:MONADO_INSTALL_DIR "openxr_monado.json")
        Add-Candidate -Candidates $candidates -Path (Join-Path $env:MONADO_INSTALL_DIR "openxr_monado-dev.json")
        Add-Candidate -Candidates $candidates -Path (Join-Path $env:MONADO_INSTALL_DIR "bin\openxr_monado.json")
    }

    Add-Candidate -Candidates $candidates -Path "$env:ProgramFiles\Monado\share\openxr\1\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "$env:ProgramFiles\Monado\share\openxr\1\openxr_monado-dev.json"
    Add-Candidate -Candidates $candidates -Path "$env:ProgramFiles\Monado\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "$env:ProgramFiles\Monado\openxr_monado-dev.json"
    Add-Candidate -Candidates $candidates -Path "${env:ProgramFiles(x86)}\Monado\share\openxr\1\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "${env:ProgramFiles(x86)}\Monado\share\openxr\1\openxr_monado-dev.json"
    Add-Candidate -Candidates $candidates -Path "$env:LOCALAPPDATA\Monado\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "$env:LOCALAPPDATA\Monado\openxr_monado-dev.json"
    Add-Candidate -Candidates $candidates -Path "Build\Deps\Monado\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "Build\Deps\Monado\openxr_monado-dev.json"
    Add-Candidate -Candidates $candidates -Path "Build\Deps\Monado\share\openxr\1\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "Build\Deps\Monado\share\openxr\1\openxr_monado-dev.json"
    Add-Candidate -Candidates $candidates -Path "Build\Submodules\monado\build\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "Build\Submodules\monado\build\openxr_monado-dev.json"
    Add-Candidate -Candidates $candidates -Path "ThirdParty\Monado\openxr_monado.json"
    Add-Candidate -Candidates $candidates -Path "ThirdParty\Monado\openxr_monado-dev.json"
}

$uniqueCandidates = $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
$errors = [System.Collections.Generic.List[string]]::new()

foreach ($candidate in $uniqueCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        continue
    }

    try {
        $manifest = Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json
        if ($null -eq $manifest.runtime) {
            throw "Manifest does not contain a runtime object."
        }

        $libraryPath = [string]$manifest.runtime.library_path
        if ([string]::IsNullOrWhiteSpace($libraryPath)) {
            throw "Manifest runtime.library_path is empty."
        }

        $resolvedLibraryPath = Resolve-RuntimeLibraryPath -ManifestPath $candidate -LibraryPath $libraryPath
        if (-not (Test-Path -LiteralPath $resolvedLibraryPath -PathType Leaf)) {
            throw "Resolved runtime library does not exist: $resolvedLibraryPath"
        }

        $runtimeVersion = ""
        $apiVersionProperty = $manifest.runtime.PSObject.Properties["api_version"]
        if ($null -ne $apiVersionProperty) {
            $runtimeVersion = [string]$apiVersionProperty.Value
        }

        [pscustomobject]@{
            RuntimeJson       = [System.IO.Path]::GetFullPath($candidate)
            RuntimeName       = [string]$manifest.runtime.name
            RuntimeVersion    = $runtimeVersion
            LibraryPath       = $resolvedLibraryPath
            ManifestDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($candidate))
        }
        return
    }
    catch {
        $errors.Add("${candidate}: $($_.Exception.Message)")
    }
}

$message = @"
Could not locate a usable Monado OpenXR runtime manifest.

Pass -RuntimeJson <path-to-openxr_monado.json>, run Tools\OpenXR\Install-Monado.ps1, set XR_RUNTIME_JSON for this process, set MONADO_RUNTIME_JSON, or set MONADO_INSTALL_DIR.
No registry values were read or written by this script.

Candidates checked:
$($uniqueCandidates -join "`n")

Manifest validation errors:
$($errors -join "`n")
"@
throw $message
