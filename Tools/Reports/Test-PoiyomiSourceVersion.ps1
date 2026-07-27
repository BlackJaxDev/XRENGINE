[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PoiyomiRoot,

    [string] $CatalogPath = "XREngine.Editor/Importers/Poiyomi/Catalogs/poiyomi-toon-9.3.64.json",

    [string] $ReportPath = "Build/_AgentValidation/poiyomi-source-version-audit.json",

    [switch] $FailOnChanges
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-IndexedItems {
    param([object[]] $Items, [string] $Key)

    $index = [ordered]@{}
    foreach ($item in $Items) {
        $name = [string]$item.$Key
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw "Catalog entry is missing key '$Key'."
        }
        $index[$name] = $item
    }
    $index
}

function Get-ChangedNames {
    param(
        [Collections.IDictionary] $Before,
        [Collections.IDictionary] $After,
        [string[]] $Fields
    )

    @(
        foreach ($name in $Before.Keys) {
            if (-not $After.Contains($name)) {
                continue
            }
            foreach ($field in $Fields) {
                $left = $Before[$name].$field | ConvertTo-Json -Depth 20 -Compress
                $right = $After[$name].$field | ConvertTo-Json -Depth 20 -Compress
                if ($left -cne $right) {
                    $name
                    break
                }
            }
        }
    ) | Sort-Object -Unique
}

function Compare-CatalogSection {
    param(
        [object[]] $BeforeItems,
        [object[]] $AfterItems,
        [string] $Key,
        [string[]] $Fields
    )

    $before = Get-IndexedItems $BeforeItems $Key
    $after = Get-IndexedItems $AfterItems $Key
    [ordered]@{
        added = @($after.Keys | Where-Object { -not $before.Contains($_) } | Sort-Object)
        removed = @($before.Keys | Where-Object { -not $after.Contains($_) } | Sort-Object)
        changed = @(Get-ChangedNames $before $after $Fields)
    }
}

$catalog = (Resolve-Path -LiteralPath $CatalogPath).Path
$generator = Join-Path $PSScriptRoot "Generate-PoiyomiToon93Catalog.ps1"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("xrengine-poiyomi-audit-" + [Guid]::NewGuid().ToString("N"))
$temporaryCatalog = Join-Path $temporaryRoot "catalog.json"
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

try {
    & $generator -PoiyomiRoot $PoiyomiRoot -OutputPath $temporaryCatalog | Out-Null
    $before = Get-Content -LiteralPath $catalog -Raw | ConvertFrom-Json
    $after = Get-Content -LiteralPath $temporaryCatalog -Raw | ConvertFrom-Json

    $propertyDiff = Compare-CatalogSection @($before.properties) @($after.properties) "name" @(
        "type", "defaultValue", "attributes", "displayOptions", "classification"
    )
    $passDiff = Compare-CatalogSection @($before.passes) @($after.passes) "name" @(
        "tags", "states", "pragmas"
    )
    $annotationDiff = Compare-CatalogSection @($before.annotations) @($after.annotations) "name" @(
        "activeUsageCount", "properties", "implementationKind"
    )
    $workflowDiff = Compare-CatalogSection @($before.workflows) @($after.workflows) "id" @(
        "kind", "label", "source"
    )
    $changeCount = @(
        $propertyDiff.added; $propertyDiff.removed; $propertyDiff.changed
        $passDiff.added; $passDiff.removed; $passDiff.changed
        $annotationDiff.added; $annotationDiff.removed; $annotationDiff.changed
        $workflowDiff.added; $workflowDiff.removed; $workflowDiff.changed
    ).Count

    $report = [ordered]@{
        formatVersion = 1
        baseline = [ordered]@{
            version = $before.source.shaderVersion
            commit = $before.source.commit
            shaderSha256 = $before.source.shaderSha256
        }
        candidate = [ordered]@{
            version = $after.source.shaderVersion
            commit = $after.source.commit
            shaderSha256 = $after.source.shaderSha256
        }
        changes = [ordered]@{
            properties = $propertyDiff
            passes = $passDiff
            annotations = $annotationDiff
            workflows = $workflowDiff
        }
        changeCount = $changeCount
        compatible = $changeCount -eq 0
        requiredReview = @(
            "Review the catalog diff and classify every added or changed entry."
            "Update parity fixtures and reference captures for affected feature families."
            "Regenerate the embedded catalog and parity documentation."
            "Run Tools/Validate-PoiyomiParity.ps1 before declaring support."
        )
    }

    $resolvedReport = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedReport)) | Out-Null
    $json = $report | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($resolvedReport, $json.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Poiyomi source audit: $changeCount catalog change(s). Report: $resolvedReport"

    if ($FailOnChanges -and $changeCount -ne 0) {
        throw "The candidate Poiyomi source differs from the supported catalog. Review the generated audit report."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryCatalog) {
        Remove-Item -LiteralPath $temporaryCatalog -Force
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Force
    }
}
