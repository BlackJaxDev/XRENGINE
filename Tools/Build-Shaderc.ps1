param(
    [string]$BuildRoot,
    [string]$OutputPath,
    [ValidateRange(1, 32)][int]$Parallel = 8
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $BuildRoot) {
    $BuildRoot = Join-Path $repoRoot 'Build/_AgentValidation/00000000-000000-shared/tools/shaderc'
}
if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'XREngine.Runtime.Rendering.Vulkan/runtimes/win-x64/native/xr_shaderc.dll'
}
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$manifest = Get-Content (Join-Path $repoRoot 'Build/Native/Shaderc/sources.json') -Raw | ConvertFrom-Json
$sourceRoot = Join-Path $BuildRoot $manifest.version
New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null
$shadercSource = Join-Path $sourceRoot 'shaderc'

foreach ($source in $manifest.sources) {
    $destination = if ($source.name -eq 'shaderc') { $shadercSource } else { Join-Path $shadercSource "third_party/$($source.name)" }
    if (Test-Path (Join-Path $destination '.xre-source-revision')) {
        if ((Get-Content (Join-Path $destination '.xre-source-revision') -Raw).Trim() -ne $source.revision) {
            throw "Source revision mismatch in $destination. Use a new build directory."
        }
        continue
    }
    $archive = Join-Path $sourceRoot "$($source.name).zip"
    if (-not (Test-Path $archive)) {
        Invoke-WebRequest "https://codeload.github.com/$($source.repo)/zip/$($source.revision)" -OutFile $archive
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $source.sha256) {
        throw "Archive checksum mismatch for $($source.name)."
    }
    $expanded = Join-Path $sourceRoot "$($source.name)-archive"
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
    $unpacked = @(Get-ChildItem -LiteralPath $expanded -Directory)
    if ($unpacked.Count -ne 1) { throw "Unexpected archive layout for $($source.name)." }
    # Both move paths must stay inside this build directory.
    foreach ($candidate in @($unpacked[0].FullName, $destination)) {
        if (-not [IO.Path]::GetFullPath($candidate).StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Source extraction path escaped the build directory.'
        }
    }
    Move-Item -LiteralPath $unpacked[0].FullName -Destination $destination
    Set-Content (Join-Path $destination '.xre-source-revision') $source.revision
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
if (-not $installation) { throw 'Install Visual Studio C++ build tools before building shaderc.' }
$major = ([version]$installation[0].installationVersion).Major
$generator = switch ($major) {
    17 { 'Visual Studio 17 2022' }
    18 { 'Visual Studio 18 2026' }
    default { throw "Unsupported Visual Studio major version: $major" }
}
$binaryRoot = Join-Path $sourceRoot 'build'
$cmake = Join-Path $installation[0].installationPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (-not (Test-Path $cmake)) { $cmake = 'cmake' }
& $cmake -S $shadercSource -B $binaryRoot -G $generator -A x64 `
    "-DCMAKE_GENERATOR_INSTANCE=$($installation[0].installationPath)" `
    -DSHADERC_SKIP_TESTS=ON -DSHADERC_SKIP_EXAMPLES=ON -DSHADERC_SKIP_COPYRIGHT_CHECK=ON `
    -DSHADERC_ENABLE_HLSL=ON -DSPIRV_SKIP_EXECUTABLES=ON -DENABLE_GLSLANG_BINARIES=OFF
if ($LASTEXITCODE -ne 0) { throw 'Shaderc configuration failed.' }
& $cmake --build $binaryRoot --config Release --target shaderc_shared --parallel $Parallel
if ($LASTEXITCODE -ne 0) { throw 'Shaderc build failed.' }
$library = Join-Path $binaryRoot 'libshaderc/Release/shaderc_shared.dll'
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath -Parent) | Out-Null
Copy-Item -LiteralPath $library -Destination $OutputPath -Force
$licensePaths = @('LICENSE', 'third_party/glslang/LICENSE.txt', 'third_party/spirv-tools/LICENSE', 'third_party/spirv-headers/LICENSE')
$notices = foreach ($relativePath in $licensePaths) {
    "===== $relativePath ====="
    Get-Content (Join-Path $shadercSource $relativePath) -Raw
}
Set-Content -LiteralPath ([IO.Path]::ChangeExtension($OutputPath, '.license.txt')) -Value ($notices -join "`n") -Encoding utf8
Write-Output "Built shaderc $($manifest.version): $OutputPath"
