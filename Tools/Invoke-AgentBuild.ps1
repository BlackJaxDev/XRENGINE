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

    # Child processes inherit the processor affinity of this process. Limit the build
    # (dotnet, MSBuild nodes, and csc with its own parallel compile) to the upper half
    # of the logical CPUs, so that the lower CPUs stay free for the desktop and other work.
    # XRE_AGENT_BUILD_AFFINITY_MASK sets a different hex mask; 0 keeps the full affinity.
    $currentProcess = [System.Diagnostics.Process]::GetCurrentProcess()
    $previousAffinity = $currentProcess.ProcessorAffinity
    $buildAffinity = [uint64]0
    if (-not [string]::IsNullOrWhiteSpace($env:XRE_AGENT_BUILD_AFFINITY_MASK)) {
        $buildAffinity = [Convert]::ToUInt64($env:XRE_AGENT_BUILD_AFFINITY_MASK.Trim() -replace '^0x', '', 16)
    }
    else {
        $logicalCpuCount = [Math]::Min([Environment]::ProcessorCount, 64)
        if ($logicalCpuCount -ge 4) {
            $allCpus = if ($logicalCpuCount -ge 64) { [uint64]::MaxValue } else { ([uint64]1 -shl $logicalCpuCount) - 1 }
            $lowerHalf = ([uint64]1 -shl [int][Math]::Floor($logicalCpuCount / 2)) - 1
            $buildAffinity = $allCpus -band (-bnot $lowerHalf)
        }
    }
    if ($buildAffinity -ne 0) {
        $buildAffinity = $buildAffinity -band [BitConverter]::ToUInt64([BitConverter]::GetBytes($previousAffinity.ToInt64()), 0)
    }
    if ($buildAffinity -ne 0) {
        $currentProcess.ProcessorAffinity = [IntPtr][BitConverter]::ToInt64([BitConverter]::GetBytes($buildAffinity), 0)
    }

    try {
        & dotnet @buildArguments
        $buildExitCode = $LASTEXITCODE
    }
    finally {
        $currentProcess.ProcessorAffinity = $previousAffinity
    }
}
finally {
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    if ($lockTaken) {
        $buildMutex.ReleaseMutex()
    }
    $buildMutex.Dispose()
}
exit $buildExitCode
