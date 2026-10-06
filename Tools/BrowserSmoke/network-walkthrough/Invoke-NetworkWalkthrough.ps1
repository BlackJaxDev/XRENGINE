# One isolated, explicitly activated Windows networking walkthrough.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Prepare', 'Run')] [string] $Mode,
    [Parameter(Mandatory)] [string] $RepoRoot,
    [Parameter(Mandatory)] [string] $RunRoot,
    [Parameter(Mandatory)] [string] $AdmissionFile,
    [string] $ActivationFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$invocationWatch = [Diagnostics.Stopwatch]::StartNew()
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 on the approved disposable Windows runner is required.' }
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('\')
$run = [IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
$nodeExe = (Get-Command node.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$ghExe = (Get-Command gh.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$dotnetExe = if ($Mode -eq 'Prepare') { (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source } else { $null }
$gate = Join-Path $PSScriptRoot 'admit-network-walkthrough.mjs'
$nodeScript = Join-Path $PSScriptRoot 'run-real-network-walkthrough.mjs'
$producer = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'producer.json') -Raw | ConvertFrom-Json
$repository = $producer.repository
$expectedCommit = $producer.sourceCommit
$publisherRunId = $producer.runId
$publisherRunAttempt = 1
function Require-Equal($Actual, $Expected, [string] $Label) {
    if ([string]$Actual -cne [string]$Expected) { throw "Approval binding mismatch: $Label" }
}
function Hash([string] $File) { (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-NoReparseAncestor([string] $Target) {
    $cursor = [IO.Path]::GetFullPath($Target)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Reparse points are not allowed in approved output or input paths.'
            }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Get-ApprovedArtifactArchive([string] $Destination) {
    # gh performs the authenticated read and follows GitHub's download redirect.
    # Copy binary stdout directly; never expose tokens or signed download URLs.
    $download = [Diagnostics.Process]::new()
    $downloadStarted = $false
    $output = $null
    try {
        $download.StartInfo.FileName = $ghExe
        $download.StartInfo.UseShellExecute = $false
        $download.StartInfo.CreateNoWindow = $true
        $download.StartInfo.RedirectStandardOutput = $true
        $download.StartInfo.RedirectStandardError = $true
        $download.StartInfo.Environment['GH_PROMPT_DISABLED'] = '1'
        foreach ($arg in @('api', "repos/$repository/actions/artifacts/$($producer.artifactId)/zip", '--hostname', 'github.com')) {
            $download.StartInfo.ArgumentList.Add($arg)
        }
        $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        if (-not $download.Start()) { throw 'Artifact download did not start.' }
        $downloadStarted = $true
        $copy = $download.StandardOutput.BaseStream.CopyToAsync($output)
        $discardErrors = $download.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $download.WaitForExit(1000)) {
            if ($watch.Elapsed.TotalSeconds -ge 180 -or $output.Length -gt 512MB) { throw 'Artifact download exceeded its bound.' }
        }
        if (-not $copy.Wait(5000) -or -not $discardErrors.Wait(5000) -or $download.ExitCode -ne 0) { throw 'Artifact download failed.' }
        $output.Flush($true)
        Require-Equal $output.Length $artifact.size_in_bytes 'downloaded archive length'
    } finally {
        try {
            if ($downloadStarted -and -not $download.HasExited) {
                $download.Kill()
                if (-not $download.WaitForExit(10000)) { throw 'Exact artifact downloader exit unconfirmed.' }
            }
        } finally { if ($output) { $output.Dispose() }; $download.Dispose() }
    }
    Require-Equal ("sha256:" + (Hash $Destination)) ('sha256:' + $producer.artifactSha256) 'downloaded archive SHA256 before extraction'
}
function Expand-ApprovedArtifactArchive([string] $Archive, [string] $Destination) {
    # Digest verification precedes this call. Still reject ZIP paths, links,
    # duplicate names and expansion beyond the deliberately bounded profile.
    if (Test-Path -LiteralPath $Destination) { throw 'Artifact extraction root already exists.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        if ($zip.Entries.Count -lt 1 -or $zip.Entries.Count -gt 20000) { throw 'Artifact ZIP entry count exceeds its bound.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $entries = [Collections.Generic.List[object]]::new()
        [long] $expandedBytes = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            $directory = $name.EndsWith('/')
            $relative = $name.TrimEnd('/')
            $parts = $relative.Split('/')
            $kind = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if (-not $relative -or $name.StartsWith('/') -or $name.Contains('\') -or
                (($entry.ExternalAttributes -band 0x400) -ne 0) -or
                $kind -notin @(0, 0x8000, 0x4000) -or ($kind -eq 0x4000 -and -not $directory) -or
                ($directory -and ($name -cne ($relative + '/') -or $kind -eq 0x8000)) -or
                ($parts | Where-Object { -not $_ -or $_ -in @('.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or
                    $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                    $_ -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)' }) -or
                -not $seen.Add($relative)) { throw 'Artifact ZIP contains an unsafe or duplicate path.' }
            if ($entry.Length -gt 512MB -or $entry.Length -lt 0 -or ($directory -and $entry.Length -ne 0)) { throw 'Artifact ZIP entry length exceeds its bound.' }
            $expandedBytes += $entry.Length
            if ($expandedBytes -gt 2GB) { throw 'Artifact ZIP expansion exceeds its bound.' }
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
            if (-not $target.StartsWith($Destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact ZIP escaped its root.' }
            $entries.Add(@{ entry = $entry; target = $target; directory = $directory })
        }
        New-Item -ItemType Directory -Path $Destination -ErrorAction Stop | Out-Null
        $buffer = [byte[]]::new(65536)
        foreach ($item in $entries) {
            if ($item.directory) { [IO.Directory]::CreateDirectory($item.target) | Out-Null; continue }
            [IO.Directory]::CreateDirectory((Split-Path -Parent $item.target)) | Out-Null
            Assert-NoReparseAncestor $item.target
            $entryStream = $null
            $output = $null
            try {
                $entryStream = $item.entry.Open()
                $output = [IO.File]::Open($item.target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                [long] $written = 0
                while (($count = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written += $count
                    if ($written -gt $item.entry.Length) { throw 'Artifact ZIP exceeded its declared entry length.' }
                    $output.Write($buffer, 0, $count)
                }
                Require-Equal $written $item.entry.Length 'extracted entry length'
            } finally { if ($output) { $output.Dispose() }; if ($entryStream) { $entryStream.Dispose() } }
        }
    } finally { $zip.Dispose() }
}
# Request verification authorizes preparation only. The separate Run branch
# repeats exact-run activation verification immediately before consuming trust.
& $nodeExe $gate verify-request --admission $AdmissionFile
if ($LASTEXITCODE -ne 0) { throw 'This is not the one admitted preparation request.' }
$admitted = Get-Content -LiteralPath $AdmissionFile -Raw | ConvertFrom-Json
Require-Equal ([IO.Path]::GetFullPath((Join-Path $repo $admitted.runRootRelative))) $run 'fixed run directory'
foreach ($localPath in @($repo, $run, $PSCommandPath, $nodeScript)) { Assert-NoReparseAncestor $localPath }
Set-Location -LiteralPath $repo
Require-Equal ((& git remote get-url origin).Trim()) 'https://github.com/BlackJaxDev/XRENGINE' 'qualified checkout repository'
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source repository.' }
Require-Equal ((& git rev-parse HEAD).Trim()) $expectedCommit 'qualified engine source commit'
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect engine source.' }
& git diff --quiet
if ($LASTEXITCODE -ne 0) { throw 'Qualified engine source has tracked changes.' }
& git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { throw 'Qualified engine source has staged changes.' }
$producerJson = & gh api "repos/$repository/actions/jobs/$($producer.jobId)" --hostname github.com
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the exact Windows publisher job.' }
$producerJob = $producerJson | ConvertFrom-Json
foreach ($entry in @(@($producerJob.id, $producer.jobId), @($producerJob.run_id, $producer.runId),
    @($producerJob.run_attempt, 1), @($producerJob.head_sha, $expectedCommit), @($producerJob.name, $producer.jobName),
    @($producerJob.status, 'completed'), @($producerJob.conclusion, 'success'))) { Require-Equal $entry[0] $entry[1] 'qualified publisher job' }
$artifactJson = & gh api "repos/$repository/actions/artifacts/$($producer.artifactId)" --hostname github.com
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the exact publisher artifact.' }
$artifact = $artifactJson | ConvertFrom-Json
foreach ($entry in @(@($artifact.id, $producer.artifactId), @($artifact.name, $producer.artifactName),
    @($artifact.size_in_bytes, $producer.artifactBytes), @($artifact.digest, ('sha256:' + $producer.artifactSha256)),
    @($artifact.workflow_run.id, $producer.runId), @($artifact.workflow_run.head_sha, $expectedCommit), @($artifact.expired, $false))) {
    Require-Equal $entry[0] $entry[1] 'qualified publisher artifact'
}
$artifactCreated = [DateTimeOffset]::Parse($artifact.created_at)
if ($artifactCreated -lt [DateTimeOffset]::Parse($producerJob.started_at) -or $artifactCreated -gt [DateTimeOffset]::Parse($producerJob.completed_at)) {
    throw 'Artifact was not created within the qualified producer job.'
}
Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Out-Null
& $nodeExe --input-type=commonjs -e 'const fs=require("node:fs"),path=require("node:path"),{createRequire}=require("node:module"); const {chromium}=createRequire(path.join(process.cwd(),"Tools/BrowserSmoke/package.json"))("playwright"); if(!fs.existsSync(chromium.executablePath()))process.exit(2);'
if ($LASTEXITCODE -ne 0) { throw 'The already approved pinned browser tooling is absent.' }
if (Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -in @(5088, 15200) }) { throw 'A required TCP port is occupied.' }
if (Get-NetUDPEndpoint -ErrorAction Stop | Where-Object { $_.LocalPort -eq 15200 }) { throw 'A required UDP port is occupied.' }
if ($Mode -eq 'Prepare') {
    if (Test-Path -LiteralPath $run) { throw 'Preparation may not reuse an existing run directory.' }
    & pwsh -NoProfile -File (Join-Path $repo 'Tools/Limit-AgentValidation.ps1') -ReserveTaskRun
    if ($LASTEXITCODE -ne 0) { throw 'Cannot reserve the isolated validation directory.' }
    New-Item -ItemType Directory -Path $run -ErrorAction Stop | Out-Null
} elseif (-not (Test-Path -LiteralPath $run -PathType Container) -or [string]::IsNullOrWhiteSpace($ActivationFile)) {
    throw 'Run requires the same prepared directory and an exact activation envelope.'
}
function Get-TreeFingerprint([string] $Root) {
    Assert-NoReparseAncestor $Root
    if (Get-ChildItem -LiteralPath $Root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'Prepared file inventory contains a reparse point.'
    }
    $paths = [string[]] @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object { $_.FullName })
    if ($paths.Length -lt 1 -or $paths.Length -gt 20000) { throw 'Prepared file inventory is invalid.' }
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    [long] $total = 0
    try {
        foreach ($file in $paths) {
            Assert-NoReparseAncestor $file
            $relative = [IO.Path]::GetRelativePath($Root, $file).Replace('\', '/')
            $length = (Get-Item -LiteralPath $file).Length
            $total += $length
            $hasher.AppendData([Text.Encoding]::UTF8.GetBytes("$relative|$length|$(Hash $file)" + [char]10))
        }
        return [ordered]@{ sha256 = [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant(); files = $paths.Length; bytes = $total }
    } finally { $hasher.Dispose() }
}
# Compiled only during the approved invocation. Unnamed handle ownership replaces
# PID/name-based tree killing. JOB_LIST assigns the process atomically at creation.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public sealed class WalkthroughOwnedJob : IDisposable {
    IntPtr job, process;
    public int ProcessId { get; private set; }
    public DateTime StartedUtc { get; private set; }
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long A,B; public uint Flags; public UIntPtr Min,Max; public uint Limit; public UIntPtr Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A,B,C,D,E,F; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic; public Io Io; public UIntPtr A,B,C,D; }
    [StructLayout(LayoutKind.Sequential)] struct Accounting { public long A,B,C,D; public uint Faults,Total,Active,Terminated; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup { public uint Size; public string Reserved,Desktop,Title; public uint X,Y,XS,YS,XC,YC,Fill,Flags; public ushort Show,Reserved2; public IntPtr ReservedPointer,In,Out,Error; }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct Info { public IntPtr Process,Thread; public uint Pid,Tid; }
    static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public WalkthroughOwnedJob() {
        job=CreateJobObjectW(IntPtr.Zero,null); Check(job!=IntPtr.Zero);
        try { var value=new Extended { Basic=new Basic { Flags=0x2000 } }; Check(SetInformationJobObject(job,9,ref value,(uint)Marshal.SizeOf<Extended>())); }
        catch { CloseHandle(job); job=IntPtr.Zero; throw; }
    }
    public void Start(string executable,string[] args,string cwd,string environmentBlock) {
        if (process!=IntPtr.Zero) throw new InvalidOperationException("Owned job already started");
        var command=new StringBuilder(Quote(executable)); foreach(var arg in args) command.Append(' ').Append(Quote(arg));
        UIntPtr size=UIntPtr.Zero; IntPtr attributes=IntPtr.Zero,jobValue=IntPtr.Zero,environment=IntPtr.Zero;
        bool initialized=false; Info info=default(Info);
        try {
            InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
            attributes=Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
            Check(InitializeProcThreadAttributeList(attributes,1,0,ref size)); initialized=true;
            jobValue=Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobValue,job);
            Check(UpdateProcThreadAttribute(attributes,0,new UIntPtr(0x2000D),jobValue,new UIntPtr((uint)IntPtr.Size),IntPtr.Zero,IntPtr.Zero));
            var start=new StartupEx { Startup=new Startup { Size=(uint)Marshal.SizeOf<StartupEx>() },Attributes=attributes };
            environment=Marshal.StringToHGlobalUni(environmentBlock);
            // Atomic job assignment, no inherited handles and no BREAKAWAY flag.
            Check(CreateProcessW(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08080404,environment,cwd,ref start,out info));
            process=info.Process; ProcessId=checked((int)info.Pid);
            using(var p=Process.GetProcessById(ProcessId)) StartedUtc=p.StartTime.ToUniversalTime();
            Check(ResumeThread(info.Thread)!=UInt32.MaxValue);
        } catch { if(process!=IntPtr.Zero) TerminateProcess(process,1); throw; }
        finally {
            if(info.Thread!=IntPtr.Zero) CloseHandle(info.Thread);
            if(initialized) DeleteProcThreadAttributeList(attributes);
            if(attributes!=IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if(jobValue!=IntPtr.Zero) Marshal.FreeHGlobal(jobValue);
            if(environment!=IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }
    static string Quote(string arg) {
        var b=new StringBuilder("\""); int slashes=0;
        foreach(char c in arg) { if(c=='\\') {slashes++;continue;} if(c=='"') b.Append('\\',slashes*2+1).Append(c); else b.Append('\\',slashes).Append(c); slashes=0; }
        return b.Append('\\',slashes*2).Append('"').ToString();
    }
    public bool Wait(int milliseconds) { uint result=WaitForSingleObject(process,(uint)milliseconds); if(result==0) return true; if(result==258) return false; Check(false);return false; }
    public uint ExitCode { get { uint code;Check(GetExitCodeProcess(process,out code));return code; } }
    public uint Active { get { Accounting value;Check(QueryInformationJobObject(job,1,out value,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero));return value.Active; } }
    public void Terminate() { Check(TerminateJobObject(job,1)); }
    public void Dispose() { if(job!=IntPtr.Zero) { CloseHandle(job);job=IntPtr.Zero; } if(process!=IntPtr.Zero) {CloseHandle(process);process=IntPtr.Zero;} }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr attr,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,ref Extended value,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,out Accounting value,uint length,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref UIntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,UIntPtr attribute,IntPtr value,UIntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessW(string application,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref StartupEx startup,out Info info);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
'@
# This exact embedded helper is covered by the approved wrapper hash. It is
# generated locally and never uploaded. A separate owned job enforces its timeout.
$cleanupScriptPath = Join-Path $run 'owned-cleanup.generated.ps1'
@'
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$mode = $args[0]
$run = $args[1]
$thumb = $args[2]
$deletePrivate = $args[3] -eq 'yes'
if ($mode -eq 'create') {
    # Private key exists only in CurrentUser/My. Record the exact public thumbprint
    # before the trust-store mutation, so interrupted Root.Add remains cleanable.
    $cert = $null
    try {
        $certificateNow = [DateTime]::UtcNow
        $certificateNow = $certificateNow.AddTicks(-($certificateNow.Ticks % [TimeSpan]::TicksPerSecond))
        $cert = New-SelfSignedCertificate -Type SSLServerAuthentication -Subject 'CN=localhost' -DnsName 'localhost' -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -CertStoreLocation 'Cert:\CurrentUser\My' -NotBefore $certificateNow -NotAfter $certificateNow.AddMinutes(30)
        $thumb = $cert.Thumbprint
        $thumb | Set-Content -LiteralPath (Join-Path $run 'certificate-thumbprint.public.txt')
        $validityMinutes = ($cert.NotAfter.ToUniversalTime() - $cert.NotBefore.ToUniversalTime()).TotalMinutes
        if (-not $cert.HasPrivateKey -or $validityMinutes -le 0 -or $validityMinutes -gt 30 -or
            $cert.NotBefore.ToUniversalTime() -ne $certificateNow -or
            $cert.NotAfter.ToUniversalTime() -ne $certificateNow.AddMinutes(30) -or
            $cert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw 'CertificateValidationFailed' }
        if (Test-Path -Path "Cert:\CurrentUser\Root\$thumb") { throw 'UnexpectedExistingTrust' }
        $publicOnly = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
        try {
            if ($publicOnly.HasPrivateKey) { throw 'UnexpectedPrivateKeyCopy' }
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $store.Add($publicOnly)
        } finally { $store.Close(); $publicOnly.Dispose() }
        exit 0
    } catch { exit 1 }
    finally { if ($cert) { $cert.Dispose() } }
}
if ($mode -ne 'cleanup') { exit 1 }
$report = [ordered]@{ certificateRemoved = $false; privateFilesRemoved = $false }
try {
    if ($thumb -ne '-') {
        if ($thumb -notmatch '^[0-9A-F]{40}$') { throw 'InvalidExactThumbprint' }
        $rootPath = "Cert:\CurrentUser\Root\$thumb"
        $myPath = "Cert:\CurrentUser\My\$thumb"
        if (Test-Path -Path $rootPath) { Remove-Item -Path $rootPath -Confirm:$false }
        if (Test-Path -Path $myPath) { Remove-Item -Path $myPath -DeleteKey -Confirm:$false }
        if ((Test-Path -Path $rootPath) -or (Test-Path -Path $myPath)) { throw 'CertificateCleanupUnconfirmed' }
    }
    $report.certificateRemoved = $true
    if ($deletePrivate) {
        $privateRoot = Join-Path $run 'private-workers'
        if (Test-Path -LiteralPath $privateRoot) {
            $root = Get-Item -LiteralPath $privateRoot -Force
            if ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'UnexpectedPrivateReparsePoint' }
            if (Get-ChildItem -LiteralPath $privateRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'UnexpectedPrivateReparsePoint' }
            Remove-Item -LiteralPath $privateRoot -Recurse -Force
        }
        $report.privateFilesRemoved = -not (Test-Path -LiteralPath $privateRoot)
    }
    if (-not $report.privateFilesRemoved) { throw 'PrivateFilesRetained' }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'cleanup-detail.public.json')
    exit 0
} catch {
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'cleanup-detail.public.json')
    exit 1
}
'@ | Set-Content -LiteralPath $cleanupScriptPath
$powerShellExe = Join-Path $PSHOME 'pwsh.exe'
# Remove tracing/credential/debug/legacy overrides BEFORE loading Playwright in
# the Node process too; sanitizing only browser/service children is insufficient.
$runtimeEnvironmentKeys = @('SYSTEMROOT','WINDIR','PATH','PATHEXT','TEMP','TMP','USERPROFILE','LOCALAPPDATA','APPDATA',
    'PROGRAMDATA','PROGRAMFILES','PROGRAMFILES(X86)','COMMONPROGRAMFILES','COMMONPROGRAMFILES(X86)','COMSPEC',
    'DOTNET_ROOT','DOTNET_ROOT_X64','PLAYWRIGHT_BROWSERS_PATH')
$runtimeEnvironmentEntries = [Collections.Generic.List[string]]::new()
foreach ($pair in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
    if ($runtimeEnvironmentKeys -contains $pair.Key) { $runtimeEnvironmentEntries.Add("$($pair.Key)=$($pair.Value)") }
}
$runtimeEnvironmentEntries.Sort([StringComparer]::OrdinalIgnoreCase)
$runtimeEnvironment = [string]::Join([char]0, $runtimeEnvironmentEntries) + [char]0 + [char]0
$buildEnvironmentEntries = [Collections.Generic.List[string]]::new($runtimeEnvironmentEntries)
$buildEnvironmentEntries.AddRange([string[]] @(
    'DOTNET_GENERATE_ASPNET_CERTIFICATE=false', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', 'DOTNET_NOLOGO=1',
    'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true', 'MSBUILDDISABLENODEREUSE=1'))
$buildEnvironmentEntries.Sort([StringComparer]::OrdinalIgnoreCase)
$buildEnvironment = [string]::Join([char]0, $buildEnvironmentEntries) + [char]0 + [char]0
$buildEvidence = [ordered]@{}
function Invoke-OwnedNativeBuild($Job, [string] $Name, [string] $Project, [string[]] $ExtraArguments) {
    # Builds share the invocation clock and leave the original browser/cleanup
    # windows inside the 30-minute preparation step. Logs remain private.
    $log = Join-Path $run "private-workers/build-$Name.log"
    $evidence = [ordered]@{ result = 'incomplete'; exitCode = $null; remainingChildren = 0; allExited = $false }
    $buildEvidence[$Name] = $evidence
    $arguments = @('build', $Project, '--configuration', 'Release', '--disable-build-servers', '-m:1', '-nr:false',
        '-p:UseSharedCompilation=false', '-p:MSBuildEnableWorkloadResolver=false',
        '-fileLogger', "-fileLoggerParameters:LogFile=$log;Verbosity=minimal") + $ExtraArguments
    if ($invocationWatch.Elapsed.TotalMinutes -ge 25) { throw 'NativeBuildBudgetExceeded' }
    $Job.Start($dotnetExe, $arguments, $repo, $buildEnvironment)
    while (-not $Job.Wait(1000)) {
        if ($invocationWatch.Elapsed.TotalMinutes -ge 25) { throw 'NativeBuildBudgetExceeded' }
        if ((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length -gt 4MB) { throw 'NativeBuildLogBoundExceeded' }
    }
    $evidence.exitCode = $Job.ExitCode
    $evidence.remainingChildren = $Job.Active
    if ($Job.Active -gt 0) { $Job.Terminate() }
    $exitWatch = [Diagnostics.Stopwatch]::StartNew()
    while ($Job.Active -gt 0 -and $exitWatch.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 100 }
    $evidence.allExited = $Job.Active -eq 0
    if (-not $evidence.allExited) { throw 'NativeBuildExitUnconfirmed' }
    if ((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length -gt 4MB) { throw 'NativeBuildLogBoundExceeded' }
    if ($evidence.exitCode -ne 0) { $evidence.result = 'failed'; throw 'Normal native build failed.' }
    $evidence.result = 'passed'
}
$site = Join-Path $run 'published-site'
$archive = Join-Path $run 'approved-publisher-artifact.zip'
$serverExe = Join-Path $repo 'XREngine.Server\bin\Release\net10.0-windows10.0.26100.0\XREngine.Server.exe'
$serviceExe = Join-Path $repo 'XREngine.ControlPlane.Service\bin\Release\net10.0-windows10.0.26100.0\XREngine.ControlPlane.Service.exe'
$certificateThumbprint = $null
$certificateJob = $null
$preflightJob = $null
$serverBuildJob = $null
$serviceBuildJob = $null
$trustAttemptConsumed = $false
$ownedJob = $null
$liveWatch = $null
$phaseSucceeded = $false
$preparedStatePath = Join-Path $run 'prepared-state.public.json'
$supervisorStage = 'DownloadApprovedArtifact'
$cleanup = [ordered]@{ allOwnedProcessesExited = $false; certificateRemoved = $false; privateFilesRemoved = $false }
try {
    if ($Mode -eq 'Prepare') {
    # Consume the exact qualified Editor output. Never regenerate another world
    # or substitute another publication after approving this immutable archive.
    Get-ApprovedArtifactArchive $archive
    $supervisorStage = 'ExtractApprovedArtifact'
    Expand-ApprovedArtifactArchive $archive $site
    $published = Join-Path $site 'content\world-package.json'
    foreach ($file in @((Join-Path $site 'index.html'), $published, (Join-Path $site 'content\manifest.json'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Approved publisher archive is incomplete.' }
    }
    $supervisorStage = 'VerifyApprovedPublishedPackage'
    # Native identity/byte preservation was proven by the qualified producer.
    # Reuse the shipping validator for complete canonical consumer verification.
    $verificationJson = & $nodeExe Tools/BrowserSmoke/verify-network-kinematic-publication.mjs --published-only $site
    if ($LASTEXITCODE -ne 0) { throw 'Canonical approved published package verification failed.' }
    $verification = $verificationJson | ConvertFrom-Json
    Require-Equal $verification.verified $true 'canonical published package verified'
    Require-Equal $verification.nativeInputCompared $false 'consumer verification scope'
    foreach ($field in @('worldSha256', 'worldBytes', 'declaredFiles', 'totalBytes', 'packageHash')) {
        Require-Equal $verification.$field $producer.$field 'qualified published package identity'
    }
    @{ repository = $repository; sourceCommit = $expectedCommit; publisherRunId = $publisherRunId;
       publisherRunAttempt = 1; publisherJobId = $producer.jobId; artifactId = $producer.artifactId;
       artifactDigest = ('sha256:' + $producer.artifactSha256); archiveBytes = $artifact.size_in_bytes;
       packageVerification = $verification } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'artifact-provenance.public.json')
    $supervisorStage = 'BuildServer'
    New-Item -ItemType Directory -Path (Join-Path $run 'private-workers') -ErrorAction Stop | Out-Null
    $serverBuildJob = [WalkthroughOwnedJob]::new()
    Invoke-OwnedNativeBuild $serverBuildJob 'server' 'XREngine.Server/XREngine.Server.csproj' @(
        '-p:XREngineRendererBackends=None', '-p:XREngineIncludeVulkanBackend=false', '-p:XREngineIncludeOpenGlBackend=false')
    $supervisorStage = 'BuildService'
    $serviceBuildJob = [WalkthroughOwnedJob]::new()
    Invoke-OwnedNativeBuild $serviceBuildJob 'service' 'XREngine.ControlPlane.Service/XREngine.ControlPlane.Service.csproj' @()
    if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf) -or -not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) { throw 'Normal executables missing.' }
    # Same published runtime and strict real first-frame gate, before any trust
    # consumption/mutation. The browser must close cleanly before continuing.
    $supervisorStage = 'BrowserPreflight'
    $preflightJob = [WalkthroughOwnedJob]::new()
    $preflightJob.Start($nodeExe, @($nodeScript, '--repo', $repo, '--run', $run, '--site', $site,
        '--manifest', $published, '--preflight-only'), $repo, $runtimeEnvironment)
    if (-not $preflightJob.Wait(180000)) { throw 'Browser preflight exceeded three minutes; certificate mutation was never reached.' }
    if ($preflightJob.ExitCode -ne 0 -or $preflightJob.Active -ne 0) { throw 'Browser preflight/close failed; certificate mutation was never reached.' }
    $preflightResult = Get-Content -LiteralPath (Join-Path $run 'preflight-result.public.json') -Raw | ConvertFrom-Json
    Require-Equal $preflightResult.result 'passed' 'preflight result'
    Require-Equal $preflightResult.checks.publishedFirstFrame $true 'real local first frame'
    Require-Equal $preflightResult.cleanup.browserClosed $true 'preflight browser closed'
    Require-Equal $preflightResult.cleanup.contextClosed $true 'preflight context closed'
    Require-Equal $preflightResult.certificateMutationReached $false 'preflight performed no certificate mutation'
    $phaseSucceeded = $true
    $supervisorStage = 'PrerequisitesPassed'
    } else {
    # A hard interruption must not leave the earlier preparation cleanup report
    # looking like successful cleanup of this live attempt.
    @{ allOwnedProcessesExited = $false; certificateRemoved = $false; privateFilesRemoved = $false;
       errors = @('LiveCleanupPending') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'cleanup.public.json')
    $supervisorStage = 'VerifyPreparedState'
    $prepared = Get-Content -LiteralPath $preparedStatePath -Raw | ConvertFrom-Json
    foreach ($entry in @(@('site', $site), @('server', (Split-Path -Parent $serverExe)), @('service', (Split-Path -Parent $serviceExe)))) {
        $fingerprint = Get-TreeFingerprint $entry[1]
        Require-Equal $fingerprint.sha256 $prepared.inventories.($entry[0]).sha256 'prepared complete file inventory'
        Require-Equal $fingerprint.files $prepared.inventories.($entry[0]).files 'prepared file count'
        Require-Equal $fingerprint.bytes $prepared.inventories.($entry[0]).bytes 'prepared byte count'
    }
    Require-Equal (Hash $archive) $producer.artifactSha256 'same approved archive'
    Require-Equal (Hash (Join-Path $run 'preflight-result.public.json')) $prepared.preflightResultSha256 'same successful browser preflight'
    Require-Equal (Hash (Join-Path $run 'supervisor-result.public.json')) $prepared.preparationResultSha256 'same successful preparation cleanup'
    & $nodeExe $gate verify-activation --admission $AdmissionFile --state $preparedStatePath --activation $ActivationFile
    if ($LASTEXITCODE -ne 0) { throw 'No current, exact-run approval authorizes this prepared trust operation.' }
    if ($invocationWatch.Elapsed.TotalSeconds -gt 120) { throw 'The live step lacks its full trust and cleanup window.' }
    $activationEnvelope = Get-Content -LiteralPath $ActivationFile -Raw | ConvertFrom-Json
    $activation = $activationEnvelope.activation
    # Every existing, failed, partial, or uncertain claim is consumed. No retries,
    # sentinel deletion, alternate run directory, rerun or automatic reactivation.
    $consumedPath = Join-Path $env:RUNNER_TEMP "xr-network-$($admitted.runId)-1-$($admitted.jobId).consumed.json"
    $binding = [ordered]@{ requestId = $admitted.requestId; requestSha256 = $admitted.requestSha256;
        sourceCommit = $expectedCommit; triggerCommit = $admitted.triggerCommit; helperCommit = $admitted.request.helperCommit;
        runId = $admitted.runId; runAttempt = 1; jobId = $admitted.jobId; preparedStateSha256 = (Hash $preparedStatePath);
        activationCommit = $activationEnvelope.activationCommit; activationSha256 = $activationEnvelope.activationSha256;
        consumedUtc = [DateTime]::UtcNow.ToString('o'); outcome = 'consumed-before-certificate' }
    $trustAttemptConsumed = $true
    $claim = $null
    try {
        $claim = [IO.File]::Open($consumedPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $bytes = [Text.Encoding]::UTF8.GetBytes(($binding | ConvertTo-Json -Compress))
        $claim.Write($bytes, 0, $bytes.Length)
        $claim.Flush($true)
    } catch { throw 'Trust claim exists, failed, or is uncertain. No retry or reuse is authorized.' }
    finally { if ($claim) { $claim.Dispose() } }
    $binding | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'consumed-binding.public.json')
    $published = Join-Path $site 'content\world-package.json'
    $supervisorStage = 'CreateLocalTrust'
    $liveWatch = [Diagnostics.Stopwatch]::StartNew()
    $certificateJob = [WalkthroughOwnedJob]::new()
    $certificateJob.Start($powerShellExe, @('-NoProfile', '-NonInteractive', '-File', $cleanupScriptPath, 'create', $run, '-', 'no'), $repo, $runtimeEnvironment)
    if (-not $certificateJob.Wait(20000)) { throw 'Certificate creation/trust exceeded its 20-second bound.' }
    if ($certificateJob.ExitCode -ne 0 -or $certificateJob.Active -ne 0) { throw 'Certificate creation/trust failed.' }
    $certificateThumbprint = (Get-Content -LiteralPath (Join-Path $run 'certificate-thumbprint.public.txt') -Raw).Trim()
    if ($certificateThumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Invalid exact certificate thumbprint.' }
    $supervisorStage = 'LiveNetworking'
    $ownedJob = [WalkthroughOwnedJob]::new()
    $nodeArgs = @($nodeScript, '--repo', $repo, '--run', $run, '--site', $site, '--manifest', $published,
        '--server', $serverExe, '--service', $serviceExe, '--thumbprint', $certificateThumbprint)
    $ownedJob.Start($nodeExe, $nodeArgs, $repo, $runtimeEnvironment)
    @{ processId = $ownedJob.ProcessId; processStartTimeUtc = $ownedJob.StartedUtc; executable = $nodeExe } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'owned-job.public.json')
    $remaining = 480000 - [int]$liveWatch.ElapsedMilliseconds
    if ($remaining -le 0 -or -not $ownedJob.Wait($remaining)) { throw 'Independent 8-minute live deadline reached.' }
    if ($ownedJob.ExitCode -ne 0) { throw 'Real network/browser walkthrough failed.' }
    $supervisorStage = 'ValidateNodeReport'
    $walkthroughResult = Get-Content -LiteralPath (Join-Path $run 'nonsecret-result.json') -Raw | ConvertFrom-Json
    Require-Equal $walkthroughResult.result 'passed' 'completed walkthrough report'
    $phaseSucceeded = $true
    $supervisorStage = 'ChecksPassed'
    }
} finally {
    $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    # Handles pin ownership; no PID/name search or environment/private-file read.
    try {
        foreach ($jobToStop in @($serverBuildJob, $serviceBuildJob, $preflightJob, $certificateJob, $ownedJob)) {
            if ($jobToStop) {
                if ($jobToStop.Active -gt 0) { $jobToStop.Terminate() }
                while ($jobToStop.Active -gt 0 -and $cleanupWatch.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 100 }
                if ($jobToStop.Active -ne 0) { throw 'OwnedJobExitUnconfirmed' }
            }
        }
        $cleanup.allOwnedProcessesExited = $true
    } catch { $cleanupErrors.Add('OwnedJobExitUnconfirmed') }
    finally {
        foreach ($jobToDispose in @($serverBuildJob, $serviceBuildJob, $preflightJob, $certificateJob, $ownedJob)) {
            if ($jobToDispose) { $jobToDispose.Dispose() }
        }
    }
    # Local certificate and file operations are isolated too: a hung provider or
    # filesystem cannot hold the supervisor indefinitely. Stop after 25 seconds.
    $cleanupJob = $null
    try {
        $cleanupJob = [WalkthroughOwnedJob]::new()
        $thumbFile = Join-Path $run 'certificate-thumbprint.public.txt'
        if (-not $certificateThumbprint -and (Test-Path -LiteralPath $thumbFile)) {
            $certificateThumbprint = (Get-Content -LiteralPath $thumbFile -Raw).Trim()
        }
        if ($certificateJob -and -not $certificateThumbprint) { $cleanupErrors.Add('CertificateIdentityUnconfirmed') }
        $thumbArgument = if ($certificateThumbprint) { $certificateThumbprint } else { '-' }
        $privateArgument = if ($cleanup.allOwnedProcessesExited) { 'yes' } else { 'no' }
        $cleanupJob.Start($powerShellExe, @('-NoProfile', '-NonInteractive', '-File', $cleanupScriptPath, 'cleanup', $run, $thumbArgument, $privateArgument), $repo, $runtimeEnvironment)
        if (-not $cleanupJob.Wait(25000)) { $cleanupJob.Terminate(); throw 'CleanupDeadlineExceeded' }
        if ($cleanupJob.ExitCode -ne 0 -or $cleanupJob.Active -ne 0) { throw 'CleanupUnconfirmed' }
        $details = Get-Content -LiteralPath (Join-Path $run 'cleanup-detail.public.json') -Raw | ConvertFrom-Json
        $cleanup.certificateRemoved = $details.certificateRemoved -eq $true
        $cleanup.privateFilesRemoved = $details.privateFilesRemoved -eq $true
        if (-not $cleanup.certificateRemoved -or -not $cleanup.privateFilesRemoved) { throw 'CleanupUnconfirmed' }
    } catch { $cleanupErrors.Add('CertificateOrPrivateCleanupUnconfirmed') }
    finally { if ($cleanupJob) { $cleanupJob.Dispose() } }
    $cleanup.elapsedSeconds = [Math]::Round($cleanupWatch.Elapsed.TotalSeconds, 3)
    if ($cleanupWatch.Elapsed.TotalSeconds -gt 60) { $cleanupErrors.Add('CleanupBudgetExceeded') }
    $cleanup.errors = @($cleanupErrors)
    $cleanup | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'cleanup.public.json')
    @{ result = $(if ($phaseSucceeded -and $cleanupErrors.Count -eq 0) { 'passed' } else { 'failed' });
       mode = $Mode; stage = $supervisorStage; trustAttemptConsumed = $trustAttemptConsumed; certificateMutationReached = ($null -ne $certificateJob); builds = $buildEvidence; cleanup = $cleanup } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'supervisor-result.public.json')
    if ($cleanupErrors.Count) { throw 'Cleanup incomplete: retain this runner privately; no retry/upload/reuse. Consult cleanup.public.json.' }
}

if ($Mode -eq 'Prepare' -and $phaseSucceeded) {
    $state = [ordered]@{ schema = 1; result = 'prepared'; requestSha256 = $admitted.requestSha256;
        triggerCommit = $admitted.triggerCommit; runId = $admitted.runId; runAttempt = 1; jobId = $admitted.jobId;
        staticResultSha256 = $admitted.staticResultSha256; sourceCommit = $expectedCommit; artifactSha256 = $producer.artifactSha256;
        certificateMutationReached = $false; preflightPassed = $true; allOwnedProcessesExited = $cleanup.allOwnedProcessesExited;
        preflightResultSha256 = (Hash (Join-Path $run 'preflight-result.public.json'));
        preparationResultSha256 = (Hash (Join-Path $run 'supervisor-result.public.json'));
        inventories = [ordered]@{ site = (Get-TreeFingerprint $site); server = (Get-TreeFingerprint (Split-Path -Parent $serverExe));
            service = (Get-TreeFingerprint (Split-Path -Parent $serviceExe)) } }
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $preparedStatePath -Encoding utf8NoBOM
}
