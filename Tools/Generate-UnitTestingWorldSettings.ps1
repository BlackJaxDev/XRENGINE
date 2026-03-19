param(
    [string]$Configuration = "Debug",
    [string]$SchemaPath = ".vscode\schemas\unit-testing-world-settings.schema.json",
    [string]$SourceFile = "XREngine.Editor\Unit Tests\Default\UnitTestingWorld.Toggles.cs",
    [string[]]$SettingsPaths = @(
        "Assets\UnitTestingWorldSettings.jsonc",
        "XREngine.Server\Assets\UnitTestingWorldSettings.jsonc"
    )
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$projectPath = Join-Path $repoRoot "Tools\UnitTestingWorldSettingsGenerator\UnitTestingWorldSettingsGenerator.csproj"

$arguments = @(
    'run',
    '--project', $projectPath,
    '-c', $Configuration,
    '--no-launch-profile',
    '--',
    '--repo-root', $repoRoot,
    '--schema-path', $SchemaPath,
    '--source-file', $SourceFile
)

foreach ($settingsPath in $SettingsPaths) {
    $arguments += @('--settings-path', $settingsPath)
}

Push-Location $repoRoot
try {
    dotnet @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}