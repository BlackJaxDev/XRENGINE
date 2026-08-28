param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("AnyCPU", "x64")]
    [string]$Platform = "AnyCPU",

    [string]$OutputPath = "docs/work/progress/runtime/runtime-modularization-phase6-consumer-api-baseline.tsv"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    Join-Path $repositoryRoot $OutputPath
}

$consumers = [ordered]@{
    "XREngine.Runtime.Bootstrap" = "XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj"
    "XREngine.Editor" = "XREngine.Editor/XREngine.Editor.csproj"
    "XREngine.Server" = "XREngine.Server/XREngine.Server.csproj"
    "XREngine.VRClient" = "XREngine.VRClient/XREngine.VRClient.csproj"
    "XREngine.UnitTests" = "XREngine.UnitTests/XREngine.UnitTests.csproj"
    "XREngine.Benchmarks" = "XREngine.Benchmarks/XREngine.Benchmarks.csproj"
    "Samples/MonkeyBallVR" = "Samples/MonkeyBallVR/MonkeyBallVR.csproj"
}

if (-not ("FacadeApiMetadataReader" -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

#nullable enable

public sealed class FacadeApiMetadataUsage
{
    public bool ReferencesFacadeAssembly { get; set; }
    public string[] TypeReferences { get; set; } = Array.Empty<string>();
    public string[] MemberReferences { get; set; } = Array.Empty<string>();
}

public static class FacadeApiMetadataReader
{
    public static FacadeApiMetadataUsage Read(string assemblyPath, string facadeAssemblyName)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader peReader = new(stream);
        MetadataReader reader = peReader.GetMetadataReader();

        bool referencesFacade = reader.AssemblyReferences
            .Select(reader.GetAssemblyReference)
            .Any(reference => reader.GetString(reference.Name) == facadeAssemblyName);

        SortedSet<string> typeReferences = new(StringComparer.Ordinal);
        foreach (TypeReferenceHandle handle in reader.TypeReferences)
        {
            TypeReference typeReference = reader.GetTypeReference(handle);
            if (ResolveAssemblyName(reader, typeReference.ResolutionScope) == facadeAssemblyName)
                typeReferences.Add(ResolveTypeName(reader, handle));
        }

        SortedSet<string> memberReferences = new(StringComparer.Ordinal);
        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference memberReference = reader.GetMemberReference(handle);
            if (memberReference.Parent.Kind != HandleKind.TypeReference)
                continue;

            TypeReferenceHandle declaringTypeHandle = (TypeReferenceHandle)memberReference.Parent;
            TypeReference declaringType = reader.GetTypeReference(declaringTypeHandle);
            if (ResolveAssemblyName(reader, declaringType.ResolutionScope) != facadeAssemblyName)
                continue;

            string declaringTypeName = ResolveTypeName(reader, declaringTypeHandle);
            memberReferences.Add($"{declaringTypeName}::{reader.GetString(memberReference.Name)}");
        }

        return new FacadeApiMetadataUsage
        {
            ReferencesFacadeAssembly = referencesFacade,
            TypeReferences = typeReferences.ToArray(),
            MemberReferences = memberReferences.ToArray(),
        };
    }

    private static string? ResolveAssemblyName(MetadataReader reader, EntityHandle scope)
        => scope.Kind switch
        {
            HandleKind.AssemblyReference => reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
            HandleKind.TypeReference => ResolveAssemblyName(reader, reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope),
            _ => null,
        };

    private static string ResolveTypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference typeReference = reader.GetTypeReference(handle);
        string name = reader.GetString(typeReference.Name);
        if (typeReference.ResolutionScope.Kind == HandleKind.TypeReference)
            return $"{ResolveTypeName(reader, (TypeReferenceHandle)typeReference.ResolutionScope)}+{name}";

        string typeNamespace = reader.GetString(typeReference.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
    }
}
'@
}

$rows = foreach ($entry in $consumers.GetEnumerator()) {
    $projectPath = Join-Path $repositoryRoot $entry.Value
    $targetPathOutput = @(
        & dotnet msbuild $projectPath -nologo -getProperty:TargetPath `
            "-p:Configuration=$Configuration" "-p:Platform=$Platform" 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve TargetPath for '$projectPath': $($targetPathOutput -join [Environment]::NewLine)"
    }

    $assemblyPath = ($targetPathOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1).Trim()
    if (-not [System.IO.File]::Exists($assemblyPath)) {
        throw "Consumer '$($entry.Key)' has not been built at '$assemblyPath'."
    }

    $usage = [FacadeApiMetadataReader]::Read($assemblyPath, "XREngine")
    [xml]$projectDocument = [System.IO.File]::ReadAllText($projectPath)
    $replacementProjectReferences = @(
        $projectDocument.Project.ItemGroup.ProjectReference.Include |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) } |
            Where-Object { $_ -notin @("XREngine", "XRENGINE") } |
            Sort-Object -Unique
    )
    [pscustomobject]@{
        ConsumerProject = $entry.Key
        ProjectPath = $entry.Value.Replace("\", "/")
        AssemblyPath = [System.IO.Path]::GetRelativePath($repositoryRoot, $assemblyPath).Replace("\", "/")
        ReferencesFacadeAssembly = $usage.ReferencesFacadeAssembly
        FacadeTypeReferenceCount = $usage.TypeReferences.Count
        FacadeMemberReferenceCount = $usage.MemberReferences.Count
        FacadeTypeReferences = $usage.TypeReferences -join ";"
        FacadeMemberReferences = $usage.MemberReferences -join ";"
        MigrationStatus = if ($usage.ReferencesFacadeAssembly) { "Pending" } else { "Migrated" }
        ReplacementProjectReferences = if ($usage.ReferencesFacadeAssembly) {
            ""
        }
        else {
            $replacementProjectReferences -join ";"
        }
    }
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$headers = @(
    "ConsumerProject",
    "AssemblyPath",
    "ReferencesFacadeAssembly",
    "FacadeTypeReferenceCount",
    "FacadeMemberReferenceCount",
    "FacadeTypeReferences",
    "FacadeMemberReferences",
    "MigrationStatus",
    "ReplacementProjectReferences",
    "ProjectPath"
)
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add(($headers -join "`t"))
foreach ($row in $rows) {
    $values = foreach ($header in $headers) {
        ([string]$row.$header).Replace("`t", " ").Replace("`r", " ").Replace("`n", " ")
    }
    $lines.Add(($values -join "`t"))
}

[System.IO.File]::WriteAllLines($resolvedOutputPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote facade API metadata for $($rows.Count) consumers to '$resolvedOutputPath'."
$rows | Format-Table ConsumerProject, ReferencesFacadeAssembly, FacadeTypeReferenceCount, FacadeMemberReferenceCount -AutoSize
