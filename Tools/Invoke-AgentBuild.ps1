# Run one agent build at a time in this Windows session.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$DotnetArguments
)

$ErrorActionPreference = 'Stop'
if ($DotnetArguments[0] -notin @('build', 'test', 'publish', 'restore', 'msbuild')) {
    throw 'Use build, test, publish, restore, or msbuild. Run applications separately with --no-build.'
}

# Use the same lock across checkouts to protect the host memory budget.
$buildMutex = [System.Threading.Mutex]::new($false, 'Local\XREngine-AgentBuild')
$lockTaken = $false
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$buildExitCode = 1
try {
    try {
        $lockTaken = $buildMutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $lockTaken = $true
    }
    if (-not $lockTaken) {
        Write-Host 'Waiting for the other agent build to finish...'
        while (-not $lockTaken) {
            try {
                $lockTaken = $buildMutex.WaitOne(500)
            }
            catch [System.Threading.AbandonedMutexException] {
                $lockTaken = $true
            }
        }
    }

    $env:MSBUILDDISABLENODEREUSE = '1'
    # Insert build options before any test-runner arguments after --.
    $separatorIndex = [Array]::IndexOf($DotnetArguments, '--')
    $buildArguments = [System.Collections.Generic.List[string]]::new()
    $argumentCount = if ($separatorIndex -lt 0) { $DotnetArguments.Length } else { $separatorIndex }
    for ($index = 0; $index -lt $argumentCount; $index++) {
        $buildArguments.Add($DotnetArguments[$index])
    }
    $buildArguments.Add('-maxCpuCount:2')
    $buildArguments.Add('-nodeReuse:false')
    $buildArguments.Add('-p:UseSharedCompilation=false')
    $buildArguments.Add('-lowPriority:true')
    if ($separatorIndex -ge 0) {
        for ($index = $separatorIndex; $index -lt $DotnetArguments.Length; $index++) {
            $buildArguments.Add($DotnetArguments[$index])
        }
    }

    & dotnet @buildArguments
    $buildExitCode = $LASTEXITCODE
}
finally {
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    if ($lockTaken) {
        $buildMutex.ReleaseMutex()
    }
    $buildMutex.Dispose()
}
exit $buildExitCode
