param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")
$projectPath = Join-Path $repoRoot "Tools\Reports\McpDocsGenerator\McpDocsGenerator.csproj"
$docPath = Join-Path $repoRoot "docs\features\mcp-server.md"

function Update-DocFromTable([string]$tableText) {
    $startMarker = '<!-- MCP_TOOL_TABLE:START -->'
    $endMarker = '<!-- MCP_TOOL_TABLE:END -->'

    $doc = Get-Content -Raw -Path $docPath
    $start = $doc.IndexOf($startMarker, [System.StringComparison]::Ordinal)
    $end = $doc.IndexOf($endMarker, [System.StringComparison]::Ordinal)

    if ($start -lt 0 -or $end -lt 0 -or $end -le $start) {
        throw "MCP docs markers not found or invalid in $docPath"
    }

    $contentStart = $start + $startMarker.Length
    $updated = $doc.Substring(0, $contentStart) + [Environment]::NewLine + [Environment]::NewLine + $tableText + [Environment]::NewLine + $doc.Substring($end)

    if ($updated -ne $doc) {
        Set-Content -Path $docPath -Value $updated -NoNewline
        Write-Host "Updated MCP tool table in $docPath"
    }
    else {
        Write-Host "MCP docs already up to date."
    }
}

function Build-TableFromSource {
    $actionFiles = Get-ChildItem -Path (Join-Path $repoRoot "XREngine.Editor\Mcp\Actions") -Filter "*.cs" -File
    $entries = @{}

    foreach ($file in $actionFiles) {
        $text = Get-Content -Raw -Path $file.FullName

        $patternA = '\[McpName\("(?<name>[^"]+)"\)\]\s*\r?\n\s*\[Description\("(?<desc>[^"]*)"\)\]'
        $patternB = '\[Description\("(?<desc>[^"]*)"\)\]\s*\r?\n\s*\[McpName\("(?<name>[^"]+)"\)\]'

        foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, $patternA)) {
            $entries[$match.Groups['name'].Value] = $match.Groups['desc'].Value
        }
        foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, $patternB)) {
            $entries[$match.Groups['name'].Value] = $match.Groups['desc'].Value
        }
    }

    $names = $entries.Keys | Sort-Object
    $lines = @('| Tool | Description |', '|------|-------------|')
    foreach ($name in $names) {
        $desc = ''
        if ($entries.ContainsKey($name)) {
            $desc = [string]$entries[$name]
        }
        $desc = $desc.Replace('|', '\\|')
        $lines += ('| `{0}` | {1} |' -f $name, $desc)
    }

    return ($lines -join [Environment]::NewLine)
}

Push-Location $repoRoot
try {
    if (Test-Path $projectPath) {
        try {
            dotnet run --project $projectPath -c $Configuration --no-launch-profile
            if ($LASTEXITCODE -eq 0) {
                exit 0
            }

            Write-Warning "Runtime generator exited with code $LASTEXITCODE. Falling back to source parser."
        }
        catch {
            Write-Warning "Runtime generator failed, falling back to source parser: $($_.Exception.Message)"
        }
    }

    $table = Build-TableFromSource
    Update-DocFromTable -tableText $table
}
finally {
    Pop-Location
}
