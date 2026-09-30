[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputFile,

    [ValidateSet('Desktop', 'Portable', 'BrowserManifest')]
    [string]$Mode = 'Desktop',

    [string]$SourcesFile,

    [string]$BrowserManifest,

    [switch]$CommandsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Remove-CSharpComments {
    param([string]$Text)

    $withoutBlock = [regex]::Replace($Text, '(?s)/\*.*?\*/', '')
    [regex]::Replace($withoutBlock, '(?m)//.*$', '')
}

function Get-CSharpNamespace {
    param([string]$Text)

    $fileScoped = [regex]::Match($Text, '(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;')
    if ($fileScoped.Success) {
        return $fileScoped.Groups[1].Value
    }

    $blockScoped = [regex]::Match($Text, '(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*(?:\{|$)')
    if ($blockScoped.Success) {
        return $blockScoped.Groups[1].Value
    }

    return ''
}

function Get-SimpleTypeName {
    param([string]$TypeName)

    $clean = ($TypeName -replace '\?.*$', '') -replace '<.*$', ''
    $constructorIndex = $clean.IndexOf('(')
    if ($constructorIndex -ge 0) {
        $clean = $clean.Substring(0, $constructorIndex)
    }

    $clean = $clean.Trim()
    if ($clean.Contains('.')) {
        return $clean.Substring($clean.LastIndexOf('.') + 1)
    }

    return $clean
}

function Split-CSharpTopLevelList {
    param([string]$Text)

    $items = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return @()
    }

    $start = 0
    $angleDepth = 0
    $parenDepth = 0
    $bracketDepth = 0

    for ($i = 0; $i -lt $Text.Length; $i++) {
        switch ($Text[$i]) {
            '<' { $angleDepth++ }
            '>' { if ($angleDepth -gt 0) { $angleDepth-- } }
            '(' { $parenDepth++ }
            ')' { if ($parenDepth -gt 0) { $parenDepth-- } }
            '[' { $bracketDepth++ }
            ']' { if ($bracketDepth -gt 0) { $bracketDepth-- } }
            ',' {
                if ($angleDepth -eq 0 -and $parenDepth -eq 0 -and $bracketDepth -eq 0) {
                    $item = $Text.Substring($start, $i - $start).Trim()
                    if (-not [string]::IsNullOrWhiteSpace($item)) {
                        [void]$items.Add($item)
                    }

                    $start = $i + 1
                }
            }
        }
    }

    $last = $Text.Substring($start).Trim()
    if (-not [string]::IsNullOrWhiteSpace($last)) {
        [void]$items.Add($last)
    }

    return $items.ToArray()
}

function Test-ParameterListMatches {
    param(
        [string]$ParameterList,
        [string[]]$ExpectedTypes
    )

    $clean = $ParameterList.Trim()
    if ($clean.StartsWith('(') -and $clean.EndsWith(')')) {
        $clean = $clean.Substring(1, $clean.Length - 2)
    }

    if ([string]::IsNullOrWhiteSpace($clean)) {
        return $ExpectedTypes.Count -eq 0
    }

    $parameters = @($clean -split ',')
    if ($parameters.Count -ne $ExpectedTypes.Count) {
        return $false
    }

    for ($i = 0; $i -lt $parameters.Count; $i++) {
        $parameter = ($parameters[$i] -replace '\s*=.*$', '').Trim()
        $parameter = [regex]::Replace($parameter, '\[[^\]]+\]\s*', '')
        $parameter = [regex]::Replace($parameter, '^(?:scoped|readonly|in|out|ref|params)\s+', '')
        $tokens = @($parameter -split '\s+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($tokens.Count -lt 2) {
            return $false
        }

        $typeName = $tokens[$tokens.Count - 2].Trim()
        $typeName = $typeName -replace '^\s*global::', ''
        $typeName = $typeName -replace '\?$', ''
        $simpleTypeName = Get-SimpleTypeName $typeName

        $expected = $ExpectedTypes[$i]
        $matches = switch ($expected) {
            'bool' { $simpleTypeName -in @('bool', 'Boolean') }
            'float' { $simpleTypeName -in @('float', 'Single') }
            default { $simpleTypeName -eq $expected }
        }

        if (-not $matches) {
            return $false
        }
    }

    return $true
}

function New-ClassInfo {
    param(
        [string]$Namespace,
        [string]$Name,
        [string]$Access,
        [string]$Modifiers,
        [bool]$IsGeneric,
        [string]$Text,
        [string]$Bases,
        [string]$PrimaryConstructorParameters,
        [bool]$SameAssembly,
        [bool]$EmitRegistration,
        [string]$RegistrationAssembly,
        [bool]$HasScriptCommandAttribute
    )

    $fullName = if ([string]::IsNullOrWhiteSpace($Namespace)) { $Name } else { "$Namespace.$Name" }
    $baseRaw = @()
    $baseSimple = @()
    if (-not [string]::IsNullOrWhiteSpace($Bases)) {
        foreach ($baseType in (Split-CSharpTopLevelList $Bases)) {
            $trimmed = $baseType.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) {
                continue
            }

            $baseRaw += $trimmed
            $baseSimple += Get-SimpleTypeName $trimmed
        }
    }

    $escapedName = [regex]::Escape($Name)
    $constructorPattern = "(?m)^\s*(?:public|internal|protected|private)\s+$escapedName\s*\("
    $hasAnyConstructor = [regex]::IsMatch($Text, $constructorPattern)
    $hasPublicParameterlessConstructor = [regex]::IsMatch($Text, "(?m)^\s*public\s+$escapedName\s*\(\s*\)")
    $hasInternalParameterlessConstructor = [regex]::IsMatch($Text, "(?m)^\s*internal\s+$escapedName\s*\(\s*\)")
    $hasFloatFloatConstructor = $false
    $hasBoolFloatFloatConstructor = $false

    if (-not [string]::IsNullOrWhiteSpace($PrimaryConstructorParameters)) {
        $hasAnyConstructor = $true
        $primaryConstructorAccessible = $Access -eq 'public' -or $SameAssembly
        if ($primaryConstructorAccessible) {
            $hasFloatFloatConstructor = Test-ParameterListMatches $PrimaryConstructorParameters @('float', 'float')
            $hasBoolFloatFloatConstructor = Test-ParameterListMatches $PrimaryConstructorParameters @('bool', 'float', 'float')
        }
    }

    foreach ($ctorMatch in [regex]::Matches($Text, "(?m)^\s*(?<access>public|internal)\s+$escapedName\s*\((?<params>[^\)]*)\)")) {
        $ctorAccess = $ctorMatch.Groups['access'].Value
        if ($ctorAccess -ne 'public' -and -not $SameAssembly) {
            continue
        }

        $parameters = $ctorMatch.Groups['params'].Value
        $hasFloatFloatConstructor = $hasFloatFloatConstructor -or (Test-ParameterListMatches $parameters @('float', 'float'))
        $hasBoolFloatFloatConstructor = $hasBoolFloatFloatConstructor -or (Test-ParameterListMatches $parameters @('bool', 'float', 'float'))
    }

    if (-not $hasAnyConstructor) {
        if ($Access -eq 'public') {
            $hasPublicParameterlessConstructor = $true
        }
        elseif ($SameAssembly) {
            $hasInternalParameterlessConstructor = $true
        }
    }

    [pscustomobject]@{
        Namespace = $Namespace
        Name = $Name
        FullName = $fullName
        Access = $Access
        Modifiers = $Modifiers
        BaseRaw = $baseRaw
        BaseSimple = $baseSimple
        SameAssembly = $SameAssembly
        EmitRegistration = $EmitRegistration
        RegistrationAssembly = $RegistrationAssembly
        HasScriptCommandAttribute = $HasScriptCommandAttribute
        IsAbstract = $Modifiers -match '(^|\s)abstract(\s|$)'
        IsStatic = $Modifiers -match '(^|\s)static(\s|$)'
        IsGeneric = $IsGeneric
        HasAccessibleParameterlessConstructor = $hasPublicParameterlessConstructor -or ($SameAssembly -and $hasInternalParameterlessConstructor)
        HasFloatFloatConstructor = $hasFloatFloatConstructor
        HasBoolFloatFloatConstructor = $hasBoolFloatFloatConstructor
        HasLocalControllerConstructor = [regex]::IsMatch($Text, "(?m)^\s*public\s+$escapedName\s*\(\s*(?:global::)?(?:XREngine\.Input\.)?ELocalPlayerIndex\s+")
        HasRemoteControllerConstructor = [regex]::IsMatch($Text, "(?m)^\s*public\s+$escapedName\s*\(\s*int\s+")
    }
}

function Add-OrMergeClassInfo {
    param(
        [hashtable]$Classes,
        [object]$Info
    )

    if (-not $Classes.ContainsKey($Info.FullName)) {
        $Classes[$Info.FullName] = $Info
        return
    }

    $existing = $Classes[$Info.FullName]
    if ($existing.RegistrationAssembly -ne $Info.RegistrationAssembly) {
        # This lexical reader also sees nested classes without their enclosing type name.
        # Ignore irrelevant simple-name collisions, but fail if either declaration can
        # participate directly in a factory hierarchy rather than guessing an owner.
        $factoryBases = @('TransformBase', 'ViewportRenderCommand', 'XRCameraParameters',
            'PostProcessSettings', 'RenderPipeline', 'PlayerController')
        if (@($existing.BaseSimple + $Info.BaseSimple | Where-Object { $_ -in $factoryBases }).Count -gt 0) {
            throw "Factory type '$($Info.FullName)' is ambiguous across registration assemblies."
        }
        return
    }
    $existing.BaseRaw = @($existing.BaseRaw + $Info.BaseRaw | Select-Object -Unique)
    $existing.BaseSimple = @($existing.BaseSimple + $Info.BaseSimple | Select-Object -Unique)
    $existing.IsAbstract = $existing.IsAbstract -or $Info.IsAbstract
    $existing.IsStatic = $existing.IsStatic -or $Info.IsStatic
    $existing.IsGeneric = $existing.IsGeneric -or $Info.IsGeneric
    $existing.HasAccessibleParameterlessConstructor = $existing.HasAccessibleParameterlessConstructor -or $Info.HasAccessibleParameterlessConstructor
    $existing.HasScriptCommandAttribute = $existing.HasScriptCommandAttribute -or $Info.HasScriptCommandAttribute
    $existing.HasFloatFloatConstructor = $existing.HasFloatFloatConstructor -or $Info.HasFloatFloatConstructor
    $existing.HasBoolFloatFloatConstructor = $existing.HasBoolFloatFloatConstructor -or $Info.HasBoolFloatFloatConstructor
    $existing.HasLocalControllerConstructor = $existing.HasLocalControllerConstructor -or $Info.HasLocalControllerConstructor
    $existing.HasRemoteControllerConstructor = $existing.HasRemoteControllerConstructor -or $Info.HasRemoteControllerConstructor
    $existing.EmitRegistration = $existing.EmitRegistration -or $Info.EmitRegistration
}

function Test-InheritsSimpleName {
    param(
        [object]$Info,
        [string]$BaseName,
        [hashtable]$BySimpleName,
        [hashtable]$Visited
    )

    if ($null -eq $Info -or $Visited.ContainsKey($Info.FullName)) {
        return $false
    }

    $Visited[$Info.FullName] = $true
    if ($Info.BaseSimple -contains $BaseName) {
        return $true
    }

    foreach ($baseSimple in $Info.BaseSimple) {
        if ($BySimpleName.ContainsKey($baseSimple) -and (Test-InheritsSimpleName $BySimpleName[$baseSimple] $BaseName $BySimpleName $Visited)) {
            return $true
        }
    }

    return $false
}

function Test-InheritsPlayerControllerOf {
    param(
        [object]$Info,
        [string]$InputTypeName,
        [hashtable]$BySimpleName,
        [hashtable]$Visited
    )

    if ($null -eq $Info -or $Visited.ContainsKey($Info.FullName)) {
        return $false
    }

    $Visited[$Info.FullName] = $true

    foreach ($baseRaw in $Info.BaseRaw) {
        if ($baseRaw -match "PlayerController\s*<\s*(?:global::)?(?:XREngine\.Input\.)?$InputTypeName\s*>") {
            return $true
        }
    }

    foreach ($baseSimple in $Info.BaseSimple) {
        if ($BySimpleName.ContainsKey($baseSimple) -and (Test-InheritsPlayerControllerOf $BySimpleName[$baseSimple] $InputTypeName $BySimpleName $Visited)) {
            return $true
        }
    }

    return $false
}

function Add-RegistrationLine {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Line
    )

    [void]$Lines.Add("        $Line")
}

if ($Mode -eq 'BrowserManifest') {
    if ($CommandsOnly -or -not [string]::IsNullOrWhiteSpace($SourcesFile) -or
        [string]::IsNullOrWhiteSpace($BrowserManifest) -or
        -not (Test-Path -LiteralPath $BrowserManifest -PathType Leaf)) {
        throw 'Browser manifest mode requires an existing manifest and no source-list or command-only mode.'
    }

    $expected = @{
        schema = 'xre.browser.registrations.v1'
        components = @{
            'xre.browser.v1.component.scene-boot' = 'XREngine.Browser.SceneBootComponent'
            'xre.browser.v1.component.mesh' = 'XREngine.Browser.BrowserMeshComponent'
            'xre.browser.v1.component.spin' = 'XREngine.Browser.BrowserSpinComponent'
        }
        transforms = @{ 'xre.browser.v1.transform.trs' = 'XREngine.Scene.Transforms.Transform' }
        resources = @{
            'xre.browser.v1.resource.mesh' = 'XREngine.Rendering.BrowserMeshData'
            'xre.browser.v1.resource.texture-rgba8' = 'XREngine.Rendering.BrowserTextureData'
            'xre.browser.v1.resource.material-unlit' = 'XREngine.Rendering.BrowserMaterialData'
            'xre.browser.v1.resource.scene-snapshot' = 'XREngine.Rendering.BrowserSceneSnapshot'
        }
        serializers = @{ 'xre.browser.v1.serializer.scene-json' = 'XREngine.Rendering.BrowserSceneSnapshot' }
        modules = @{ 'xre.browser.v1.module.webgpu' = 'XREngine.Rendering.WebGPU.WebGpuRendererBackendModule' }
    }
    $manifest = Get-Content -LiteralPath $BrowserManifest -Raw | ConvertFrom-Json
    if (@($manifest.PSObject.Properties).Count -ne $expected.Count) { throw 'Browser registration manifest differs from the approved allow-list.' }
    foreach ($category in $expected.Keys) {
        if (-not (@($manifest.PSObject.Properties.Name) -contains $category)) { throw "Browser registration manifest lacks '$category'." }
        if ($category -eq 'schema') {
            if ($manifest.schema -cne $expected.schema) { throw 'Browser registration schema differs from the approved version.' }
            continue
        }
        $actualValues = $manifest.PSObject.Properties[$category].Value
        $expectedValues = $expected[$category]
        if (@($actualValues.PSObject.Properties).Count -ne $expectedValues.Count) { throw "Browser registration '$category' differs from the approved allow-list." }
        foreach ($id in $expectedValues.Keys) {
            if (-not (@($actualValues.PSObject.Properties.Name) -contains $id) -or
                $actualValues.PSObject.Properties[$id].Value -cne $expectedValues[$id]) {
                throw "Browser registration '$id' differs from the approved type/signature allow-list."
            }
        }
    }

    $template = Join-Path $PSScriptRoot '../Build/Registration/BrowserStaticRegistrations.template.txt'
    $content = (Get-Content -LiteralPath $template -Raw).Replace("`r`n", "`n")
    if (-not $content.EndsWith("`n")) { $content += "`n" }
    $outputDirectory = Split-Path -Parent $OutputFile
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    if ((Test-Path -LiteralPath $OutputFile) -and (Get-Content -LiteralPath $OutputFile -Raw) -ceq $content) { return }
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputFile), $content, [System.Text.UTF8Encoding]::new($false))
    return
}

$projectFullPath = [System.IO.Path]::GetFullPath($ProjectDir)
$projectAssembly = Split-Path -Leaf $projectFullPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$pathComparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
$sourceEntries = [System.Collections.Generic.List[object]]::new()

if (-not [string]::IsNullOrWhiteSpace($SourcesFile)) {
    if (-not (Test-Path -LiteralPath $SourcesFile -PathType Leaf)) {
        throw "Registration source list is missing: $SourcesFile"
    }

    $seenSources = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [System.IO.File]::ReadAllLines([System.IO.Path]::GetFullPath($SourcesFile))) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $fields = $line.Split('|')
        if ($fields.Length -ne 3 -or $fields[1] -notmatch '^XREngine(?:\.[A-Za-z0-9]+)+$' -or $fields[2] -notin @('true', 'false')) {
            throw "Invalid registration source row: $line"
        }

        $file = [System.IO.Path]::GetFullPath($fields[0])
        if (-not $file.StartsWith($repositoryRoot, $pathComparison) -or
            -not $file.EndsWith('.cs', [System.StringComparison]::OrdinalIgnoreCase) -or
            $file -match '[\\/](?:bin|obj)[\\/]' -or
            $file.EndsWith('.g.cs', [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $file -PathType Leaf) -or
            -not $seenSources.Add($file)) {
            throw "Registration source is missing, duplicate, generated, or outside the repository: $file"
        }

        [void]$sourceEntries.Add([pscustomobject]@{
            Path = $file
            Assembly = $fields[1]
            Emit = $fields[2] -eq 'true'
        })
    }
    if ($sourceEntries.Count -eq 0) { throw 'Registration source list is empty.' }
}

$sourceRoots = New-Object System.Collections.Generic.List[string]
$sourceRootNames = if ($CommandsOnly) { @('.') } else { @(
    '.',
    '..\XREngine.Data',
    '..\XREngine.Animation',
    '..\XREngine.Runtime.Core',
    '..\XREngine.Runtime.Rendering',
    '..\XREngine.Runtime.UI.Skia',
    '..\XREngine.Runtime.UI.Rive',
    '..\XREngine.Audio.Audio2Face',
    '..\XREngine.Audio.SteamAudio',
    '..\XREngine.Runtime.Physics.PhysX',
    '..\XREngine.Runtime.Net.Osc',
    '..\XREngine.Runtime.Net.Sockets',
    '..\XREngine.Runtime.AnimationIntegration',
    '..\XREngine.Runtime.AudioIntegration',
    '..\XREngine.Runtime.InputIntegration',
    '..\XREngine.Runtime.ModelAssetPipeline',
    '..\XREngine.Runtime.ModelingIntegration'
) }

if ($sourceEntries.Count -eq 0) {
    foreach ($sourceRootName in $sourceRootNames) {
        $sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $projectFullPath $sourceRootName))
        if (Test-Path $sourceRoot) {
            [void]$sourceRoots.Add($sourceRoot)
        }
    }
}

$classes = @{}
$classRegex = [regex]::new('(?m)^\s*(?:\[[^\r\n]*\]\s*)*(?<access>public|internal)\s+(?<mods>(?:(?:new|sealed|abstract|partial|static)\s+)*)class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<generic>\s*<[^>{;]+>)?(?<primary>\s*\([^\)]*\))?\s*(?::\s*(?<bases>[^\{\r\n]+))?', [System.Text.RegularExpressions.RegexOptions]::Multiline)

if ($sourceEntries.Count -eq 0) {
    foreach ($sourceRoot in $sourceRoots) {
        $sameAssembly = [System.StringComparer]::OrdinalIgnoreCase.Equals($sourceRoot, $projectFullPath)
        foreach ($file in Get-ChildItem -Path $sourceRoot -Recurse -Filter '*.cs' -File) {
            $fullPath = $file.FullName
            if ($fullPath -match '[\\/](bin|obj)[\\/]' -or $file.Name.EndsWith('.g.cs', [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            [void]$sourceEntries.Add([pscustomobject]@{
                Path = $fullPath
                Assembly = if ($sameAssembly) { $projectAssembly } else { Split-Path -Leaf $sourceRoot }
                Emit = $true
            })
        }
    }
}

$entryByPath = @{}
foreach ($entry in $sourceEntries) { $entryByPath[$entry.Path] = $entry }
$sortedPaths = [string[]]@($sourceEntries | ForEach-Object { $_.Path })
[System.Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
foreach ($path in $sortedPaths) {
    $entry = $entryByPath[$path]
    $scanText = Remove-CSharpComments (Get-Content -LiteralPath $entry.Path -Raw)
    $namespace = Get-CSharpNamespace $scanText

    foreach ($match in $classRegex.Matches($scanText)) {
        $info = New-ClassInfo `
            -Namespace $namespace `
            -Name $match.Groups['name'].Value `
            -Access $match.Groups['access'].Value `
            -Modifiers $match.Groups['mods'].Value `
            -IsGeneric:$match.Groups['generic'].Success `
            -Text $scanText `
            -Bases $match.Groups['bases'].Value `
            -PrimaryConstructorParameters $match.Groups['primary'].Value `
            -SameAssembly:($entry.Assembly -eq $projectAssembly) `
            -EmitRegistration:$entry.Emit `
            -RegistrationAssembly $entry.Assembly `
            -HasScriptCommandAttribute:([regex]::IsMatch($match.Value, '\[RenderPipelineScriptCommand(?:Attribute)?\s*\]'))

        Add-OrMergeClassInfo $classes $info
    }
}

$bySimpleName = @{}
foreach ($info in @($classes.Values | Sort-Object FullName -CaseSensitive)) {
    if (-not $bySimpleName.ContainsKey($info.Name)) {
        $bySimpleName[$info.Name] = $info
    }
}

$concreteTypes = @($classes.Values | Where-Object { $_.EmitRegistration -and -not $_.IsAbstract -and -not $_.IsStatic -and -not $_.IsGeneric })

$transformTypes = @($concreteTypes |
    Where-Object { $_.HasAccessibleParameterlessConstructor -and (Test-InheritsSimpleName $_ 'TransformBase' $bySimpleName @{}) } |
    Sort-Object FullName)

$commandTypes = @($concreteTypes |
    Where-Object { $_.HasAccessibleParameterlessConstructor -and ($CommandsOnly -or $_.RegistrationAssembly -ne 'XREngine.Runtime.Rendering') -and (Test-InheritsSimpleName $_ 'ViewportRenderCommand' $bySimpleName @{}) } |
    Sort-Object FullName)

if ($CommandsOnly) {
    $commandLines = [System.Collections.Generic.List[string]]::new()
    [void]$commandLines.Add('// <auto-generated />')
    [void]$commandLines.Add('#pragma warning disable CA2255')
    [void]$commandLines.Add('using System.Runtime.CompilerServices;')
    [void]$commandLines.Add('using XREngine.Rendering;')
    [void]$commandLines.Add('using XREngine.Rendering.Pipelines.Commands;')
    [void]$commandLines.Add('namespace XREngine.Generated;')
    [void]$commandLines.Add('internal static class GeneratedRenderCommandRegistrations')
    [void]$commandLines.Add('{')
    [void]$commandLines.Add('    [ModuleInitializer]')
    [void]$commandLines.Add('    internal static void Register()')
    [void]$commandLines.Add('    {')
    foreach ($info in $commandTypes) {
        Add-RegistrationLine $commandLines "ViewportRenderCommandContainer.RegisterBuiltInCommandFactory(typeof(global::$($info.FullName)), static () => new global::$($info.FullName)());"
        if ($info.HasScriptCommandAttribute) {
            Add-RegistrationLine $commandLines "RenderPipelineScript.RegisterCommandScriptName(typeof(global::$($info.FullName)), null, true);"
        }
    }
    [void]$commandLines.Add('    }')
    [void]$commandLines.Add('}')
    $content = ($commandLines -join "`n") + "`n"
    $outputDirectory = Split-Path -Parent $OutputFile
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    if ((Test-Path $OutputFile) -and ((Get-Content -LiteralPath $OutputFile -Raw) -eq $content)) {
        return
    }
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputFile), $content, [System.Text.UTF8Encoding]::new($false))
    return
}

$cameraParameterTypes = @($concreteTypes |
    Where-Object {
        ($_.HasAccessibleParameterlessConstructor -or $_.HasFloatFloatConstructor -or $_.HasBoolFloatFloatConstructor) -and
        (Test-InheritsSimpleName $_ 'XRCameraParameters' $bySimpleName @{})
    } |
    Sort-Object FullName)

$postProcessBackingTypes = @($concreteTypes |
    Where-Object { $_.HasAccessibleParameterlessConstructor -and (Test-InheritsSimpleName $_ 'PostProcessSettings' $bySimpleName @{}) } |
    Sort-Object FullName)

$pipelineTypes = @($concreteTypes |
    Where-Object { $_.HasAccessibleParameterlessConstructor -and (Test-InheritsSimpleName $_ 'RenderPipeline' $bySimpleName @{}) } |
    Sort-Object FullName)

$localControllerTypes = @($concreteTypes |
    Where-Object { $_.Access -eq 'public' -and $_.HasLocalControllerConstructor -and (Test-InheritsPlayerControllerOf $_ 'LocalInputInterface' $bySimpleName @{}) } |
    Sort-Object FullName)

$remoteControllerTypes = @($concreteTypes |
    Where-Object { $_.Access -eq 'public' -and $_.HasRemoteControllerConstructor -and (Test-InheritsPlayerControllerOf $_ 'ServerInputInterface' $bySimpleName @{}) } |
    Sort-Object FullName)

$lines = [System.Collections.Generic.List[string]]::new()
[void]$lines.Add('// <auto-generated />')
[void]$lines.Add('#nullable enable')
[void]$lines.Add('#pragma warning disable CA2255')
[void]$lines.Add('using System.Diagnostics.CodeAnalysis;')
[void]$lines.Add('using System.Runtime.CompilerServices;')
[void]$lines.Add('using XREngine.Input;')
[void]$lines.Add('using XREngine.Rendering;')
[void]$lines.Add('using XREngine.Rendering.Pipelines.Commands;')
[void]$lines.Add('using XREngine.Rendering.PostProcessing;')
[void]$lines.Add('using XREngine.Scene.Transforms;')
[void]$lines.Add('')
[void]$lines.Add('namespace XREngine.Generated;')
[void]$lines.Add('')
[void]$lines.Add('[SuppressMessage("Usage", "CA2255:The ModuleInitializer attribute is intentionally used for generated engine registration.", Justification = "Generated AOT factories must register when the engine assembly loads.")]')
[void]$lines.Add('internal static class GeneratedAotFactoryRegistrations')
[void]$lines.Add('{')
[void]$lines.Add('    [ModuleInitializer]')
[void]$lines.Add('    internal static void Register()')
[void]$lines.Add('    {')

foreach ($info in $transformTypes) {
    Add-RegistrationLine $lines "TransformFactoryRegistry.Register(typeof(global::$($info.FullName)), static () => new global::$($info.FullName)());"
}

foreach ($info in $commandTypes) {
    Add-RegistrationLine $lines "ViewportRenderCommandContainer.RegisterBuiltInCommandFactory(typeof(global::$($info.FullName)), static () => new global::$($info.FullName)());"
    if ($info.HasScriptCommandAttribute) {
        Add-RegistrationLine $lines "RenderPipelineScript.RegisterCommandScriptName(typeof(global::$($info.FullName)), null, true);"
    }
}

foreach ($info in $cameraParameterTypes) {
    if ($info.HasFloatFloatConstructor) {
        Add-RegistrationLine $lines "XRCameraParameters.RegisterFactory(typeof(global::$($info.FullName)), static (nearZ, farZ) => new global::$($info.FullName)(nearZ, farZ));"
    }
    elseif ($info.HasBoolFloatFloatConstructor) {
        Add-RegistrationLine $lines "XRCameraParameters.RegisterFactory(typeof(global::$($info.FullName)), static (nearZ, farZ) => new global::$($info.FullName)(true, nearZ, farZ));"
    }
    else {
        Add-RegistrationLine $lines "XRCameraParameters.RegisterFactory(typeof(global::$($info.FullName)), static (_, _) => new global::$($info.FullName)());"
    }
}

foreach ($info in $postProcessBackingTypes) {
    Add-RegistrationLine $lines "PostProcessBackingFactoryRegistry.Register(typeof(global::$($info.FullName)), static () => new global::$($info.FullName)());"
}

foreach ($info in $pipelineTypes) {
    Add-RegistrationLine $lines "RenderPipeline.RegisterOpenXrPipelineFactory(typeof(global::$($info.FullName)), static source => new global::$($info.FullName) { IsShadowPass = source.IsShadowPass });"
}

for ($i = 0; $i -lt $localControllerTypes.Count; $i++) {
    $info = $localControllerTypes[$i]
    $makeDefault = if ($i -eq 0) { 'true' } else { 'false' }
    Add-RegistrationLine $lines "RuntimePlayerControllerServices.RegisterLocalControllerFactory(typeof(global::$($info.FullName)), static index => new global::$($info.FullName)(index), makeDefault: $makeDefault);"
}

for ($i = 0; $i -lt $remoteControllerTypes.Count; $i++) {
    $info = $remoteControllerTypes[$i]
    $makeDefault = if ($i -eq 0) { 'true' } else { 'false' }
    Add-RegistrationLine $lines "RuntimePlayerControllerServices.RegisterRemoteControllerFactory(typeof(global::$($info.FullName)), static serverPlayerIndex => new global::$($info.FullName)(serverPlayerIndex), makeDefault: $makeDefault);"
}

[void]$lines.Add('    }')
[void]$lines.Add('}')

$content = ($lines -join "`n") + "`n"
$outputDirectory = Split-Path -Parent $OutputFile
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

if ((Test-Path $OutputFile) -and ((Get-Content -LiteralPath $OutputFile -Raw) -eq $content)) {
    return
}

[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputFile), $content, [System.Text.UTF8Encoding]::new($false))
