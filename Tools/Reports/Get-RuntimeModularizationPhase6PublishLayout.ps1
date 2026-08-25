param(
    [string]$PublishRoot = "Build/_AgentValidation/20260825-103925-runtime-modularization-p60/temp-build/publish",

    [string]$OutputPath = "docs/work/progress/runtime/runtime-modularization-phase6-publish-layout-baseline.tsv"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolvedPublishRoot = if ([System.IO.Path]::IsPathRooted($PublishRoot)) {
    $PublishRoot
}
else {
    Join-Path $repositoryRoot $PublishRoot
}
$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    Join-Path $repositoryRoot $OutputPath
}

$applications = [ordered]@{
    "Editor" = "editor"
    "Server" = "server"
    "VRClient" = "vrclient"
}

function Get-FileCategory {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$File
    )

    switch ($File.Extension.ToLowerInvariant()) {
        ".dll" {
            try {
                [void][System.Reflection.AssemblyName]::GetAssemblyName($File.FullName)
                return "ManagedAssembly"
            }
            catch [System.BadImageFormatException] {
                return "NativeLibrary"
            }
            catch [System.IO.FileLoadException] {
                return "NativeLibrary"
            }
        }
        ".exe" { return "NativeHost" }
        ".so" { return "NativeLibrary" }
        ".dylib" { return "NativeLibrary" }
        ".pdb" { return "Symbols" }
        ".json" { return "ConfigurationOrManifest" }
        ".jsonc" { return "ConfigurationOrManifest" }
        ".config" { return "ConfigurationOrManifest" }
        default { return "Content" }
    }
}

$rows = foreach ($entry in $applications.GetEnumerator()) {
    $applicationRoot = Join-Path $resolvedPublishRoot $entry.Value
    if (-not [System.IO.Directory]::Exists($applicationRoot)) {
        throw "Publish layout for '$($entry.Key)' does not exist at '$applicationRoot'."
    }

    foreach ($file in Get-ChildItem -LiteralPath $applicationRoot -Recurse -File | Sort-Object FullName) {
        [pscustomobject]@{
            Application = $entry.Key
            RelativePath = [System.IO.Path]::GetRelativePath($applicationRoot, $file.FullName).Replace("\", "/")
            Category = Get-FileCategory $file
            SizeBytes = $file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$headers = @("Application", "RelativePath", "Category", "SizeBytes", "Sha256")
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add(($headers -join "`t"))
foreach ($row in $rows) {
    $lines.Add((@(
        $row.Application,
        $row.RelativePath,
        $row.Category,
        $row.SizeBytes,
        $row.Sha256
    ) -join "`t"))
}

[System.IO.File]::WriteAllLines($resolvedOutputPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($rows.Count) publish-layout rows to '$resolvedOutputPath'."
$rows |
    Group-Object Application, Category |
    Sort-Object Name |
    ForEach-Object {
        [pscustomobject]@{
            Application = $_.Group[0].Application
            Category = $_.Group[0].Category
            Files = $_.Count
            Bytes = ($_.Group | Measure-Object SizeBytes -Sum).Sum
        }
    } |
    Format-Table -AutoSize
