param(
    [string]$OutputPath = "docs/work/progress/runtime/runtime-modularization-phase6-project-graph-baseline.tsv"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    Join-Path $repositoryRoot $OutputPath
}

$projects = @(
    [pscustomobject]@{ Name = "XREngine.Extensions"; Path = "XREngine.Extensions/XREngine.Extensions.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Data"; Path = "XREngine.Data/XREngine.Data.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Animation"; Path = "XREngine.Animation/XREngine.Animation.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Audio"; Path = "XREngine.Audio/XREngine.Audio.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Modeling"; Path = "XREngine.Modeling/XREngine.Modeling.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Fbx"; Path = "XREngine.Fbx/XREngine.Fbx.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Gltf"; Path = "XREngine.Gltf/XREngine.Gltf.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Input"; Path = "XREngine.Input/XREngine.Input.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.Core"; Path = "XREngine.Runtime.Core/XREngine.Runtime.Core.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.Rendering"; Path = "XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.Rendering.OpenGL"; Path = "XREngine.Runtime.Rendering.OpenGL/XREngine.Runtime.Rendering.OpenGL.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.Rendering.Vulkan"; Path = "XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.AnimationIntegration"; Path = "XREngine.Runtime.AnimationIntegration/XREngine.Runtime.AnimationIntegration.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.AudioIntegration"; Path = "XREngine.Runtime.AudioIntegration/XREngine.Runtime.AudioIntegration.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.InputIntegration"; Path = "XREngine.Runtime.InputIntegration/XREngine.Runtime.InputIntegration.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.ModelAssetPipeline"; Path = "XREngine.Runtime.ModelAssetPipeline/XREngine.Runtime.ModelAssetPipeline.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.ModelingIntegration"; Path = "XREngine.Runtime.ModelingIntegration/XREngine.Runtime.ModelingIntegration.csproj"; Role = "Destination" },
    [pscustomobject]@{ Name = "XREngine.Runtime.Bootstrap"; Path = "XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj"; Role = "Destination;Consumer" },
    [pscustomobject]@{ Name = "XREngine.Editor"; Path = "XREngine.Editor/XREngine.Editor.csproj"; Role = "Destination;Consumer" },
    [pscustomobject]@{ Name = "XREngine.Server"; Path = "XREngine.Server/XREngine.Server.csproj"; Role = "Consumer" },
    [pscustomobject]@{ Name = "XREngine.VRClient"; Path = "XREngine.VRClient/XREngine.VRClient.csproj"; Role = "Consumer" },
    [pscustomobject]@{ Name = "XREngine.UnitTests"; Path = "XREngine.UnitTests/XREngine.UnitTests.csproj"; Role = "Consumer" },
    [pscustomobject]@{ Name = "XREngine.Benchmarks"; Path = "XREngine.Benchmarks/XREngine.Benchmarks.csproj"; Role = "Consumer" },
    [pscustomobject]@{ Name = "Samples/MonkeyBallVR"; Path = "Samples/MonkeyBallVR/MonkeyBallVR.csproj"; Role = "Consumer" },
    [pscustomobject]@{ Name = "XREngine facade"; Path = "XRENGINE/XREngine.csproj"; Role = "RemovalSource" }
)

$rows = foreach ($project in $projects) {
    $projectPath = Join-Path $repositoryRoot $project.Path
    [xml]$xml = [System.IO.File]::ReadAllText($projectPath)
    $projectDirectory = Split-Path -Parent $project.Path
    $sourceFiles = @(
        & git -C $repositoryRoot ls-files --cached --others --exclude-standard -- `
            "$projectDirectory/*.cs" "$projectDirectory/**/*.cs"
    )
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed for '$($project.Path)'."
    }

    $projectReferences = @(
        $xml.Project.ItemGroup.ProjectReference |
            Where-Object Include |
            ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension([string]$_.Include) } |
            Sort-Object -Unique
    )
    $packageReferences = @(
        $xml.Project.ItemGroup.PackageReference |
            Where-Object Include |
            ForEach-Object { [string]$_.Include } |
            Sort-Object -Unique
    )
    $packageUpdates = @(
        $xml.Project.ItemGroup.PackageReference |
            Where-Object Update |
            ForEach-Object { [string]$_.Update } |
            Sort-Object -Unique
    )
    $friendAssemblies = @(
        $xml.Project.ItemGroup.InternalsVisibleTo |
            Where-Object Include |
            ForEach-Object { [string]$_.Include } |
            Sort-Object -Unique
    )
    $targets = @(
        $xml.Project.Target |
            Where-Object Name |
            ForEach-Object { [string]$_.Name } |
            Sort-Object -Unique
    )
    $framework = @(
        [string]$xml.Project.PropertyGroup.TargetFramework,
        [string]$xml.Project.PropertyGroup.TargetFrameworks
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1

    [pscustomobject]@{
        Project = $project.Name
        Role = $project.Role
        ProjectPath = $project.Path
        TargetFramework = $framework
        RepositoryCSharpFiles = $sourceFiles.Count
        ProjectReferences = $projectReferences -join ";"
        PackageReferences = $packageReferences -join ";"
        PackageReferenceUpdates = $packageUpdates -join ";"
        FriendAssemblies = $friendAssemblies -join ";"
        CustomTargets = $targets -join ";"
    }
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$headers = @(
    "Project",
    "Role",
    "TargetFramework",
    "RepositoryCSharpFiles",
    "ProjectReferences",
    "PackageReferences",
    "PackageReferenceUpdates",
    "FriendAssemblies",
    "CustomTargets",
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
Write-Host "Wrote $($rows.Count) project-graph rows to '$resolvedOutputPath'."
$rows | Select-Object Project, Role, RepositoryCSharpFiles, ProjectReferences | Format-Table -Wrap
