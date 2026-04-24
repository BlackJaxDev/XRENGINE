[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputFile
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
        [bool]$SameAssembly
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
    $existing.BaseRaw = @($existing.BaseRaw + $Info.BaseRaw | Select-Object -Unique)
    $existing.BaseSimple = @($existing.BaseSimple + $Info.BaseSimple | Select-Object -Unique)
    $existing.IsAbstract = $existing.IsAbstract -or $Info.IsAbstract
    $existing.IsStatic = $existing.IsStatic -or $Info.IsStatic
    $existing.IsGeneric = $existing.IsGeneric -or $Info.IsGeneric
    $existing.HasAccessibleParameterlessConstructor = $existing.HasAccessibleParameterlessConstructor -or $Info.HasAccessibleParameterlessConstructor
    $existing.HasFloatFloatConstructor = $existing.HasFloatFloatConstructor -or $Info.HasFloatFloatConstructor
    $existing.HasBoolFloatFloatConstructor = $existing.HasBoolFloatFloatConstructor -or $Info.HasBoolFloatFloatConstructor
    $existing.HasLocalControllerConstructor = $existing.HasLocalControllerConstructor -or $Info.HasLocalControllerConstructor
    $existing.HasRemoteControllerConstructor = $existing.HasRemoteControllerConstructor -or $Info.HasRemoteControllerConstructor
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

$projectFullPath = [System.IO.Path]::GetFullPath($ProjectDir)
$sourceRoots = New-Object System.Collections.Generic.List[string]
[void]$sourceRoots.Add($projectFullPath)

$inputIntegrationRoot = [System.IO.Path]::GetFullPath((Join-Path $projectFullPath '..\XREngine.Runtime.InputIntegration'))
if (Test-Path $inputIntegrationRoot) {
    [void]$sourceRoots.Add($inputIntegrationRoot)
}

$classes = @{}
$classRegex = [regex]::new('(?m)^\s*(?:\[[^\r\n]*\]\s*)*(?<access>public|internal)\s+(?<mods>(?:(?:new|sealed|abstract|partial|static)\s+)*)class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<generic>\s*<[^>{;]+>)?(?<primary>\s*\([^\)]*\))?\s*(?::\s*(?<bases>[^\{\r\n]+))?', [System.Text.RegularExpressions.RegexOptions]::Multiline)

foreach ($sourceRoot in $sourceRoots) {
    $sameAssembly = [System.StringComparer]::OrdinalIgnoreCase.Equals($sourceRoot, $projectFullPath)
    foreach ($file in Get-ChildItem -Path $sourceRoot -Recurse -Filter '*.cs' -File) {
        $fullPath = $file.FullName
        if ($fullPath -match '\\(bin|obj)\\' -or $file.Name.EndsWith('.g.cs', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $text = Get-Content -LiteralPath $fullPath -Raw
        $scanText = Remove-CSharpComments $text
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
                -SameAssembly:$sameAssembly

            Add-OrMergeClassInfo $classes $info
        }
    }
}

$bySimpleName = @{}
foreach ($info in $classes.Values) {
    if (-not $bySimpleName.ContainsKey($info.Name)) {
        $bySimpleName[$info.Name] = $info
    }
}

$concreteTypes = @($classes.Values | Where-Object { -not $_.IsAbstract -and -not $_.IsStatic -and -not $_.IsGeneric })

$transformTypes = @($concreteTypes |
    Where-Object { $_.SameAssembly -and $_.HasAccessibleParameterlessConstructor -and (Test-InheritsSimpleName $_ 'TransformBase' $bySimpleName @{}) } |
    Sort-Object FullName)

$commandTypes = @($concreteTypes |
    Where-Object { $_.SameAssembly -and $_.HasAccessibleParameterlessConstructor -and (Test-InheritsSimpleName $_ 'ViewportRenderCommand' $bySimpleName @{}) } |
    Sort-Object FullName)

$cameraParameterTypes = @($concreteTypes |
    Where-Object {
        $_.SameAssembly -and
        ($_.HasAccessibleParameterlessConstructor -or $_.HasFloatFloatConstructor -or $_.HasBoolFloatFloatConstructor) -and
        (Test-InheritsSimpleName $_ 'XRCameraParameters' $bySimpleName @{})
    } |
    Sort-Object FullName)

$postProcessBackingTypes = @($concreteTypes |
    Where-Object { $_.SameAssembly -and $_.HasAccessibleParameterlessConstructor -and (Test-InheritsSimpleName $_ 'PostProcessSettings' $bySimpleName @{}) } |
    Sort-Object FullName)

$pipelineTypes = @($concreteTypes |
    Where-Object { $_.SameAssembly -and $_.HasAccessibleParameterlessConstructor -and (Test-InheritsSimpleName $_ 'RenderPipeline' $bySimpleName @{}) } |
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
    Add-RegistrationLine $lines "ViewportRenderCommandContainer.RegisterCommandFactory(typeof(global::$($info.FullName)), static () => new global::$($info.FullName)());"
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

$content = ($lines -join [Environment]::NewLine) + [Environment]::NewLine
$outputDirectory = Split-Path -Parent $OutputFile
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

if ((Test-Path $OutputFile) -and ((Get-Content -LiteralPath $OutputFile -Raw) -eq $content)) {
    return
}

Set-Content -LiteralPath $OutputFile -Value $content -Encoding UTF8
