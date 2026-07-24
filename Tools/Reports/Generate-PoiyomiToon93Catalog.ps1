[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PoiyomiRoot,

    [string]$OutputPath = "XRENGINE/Scene/Importers/Poiyomi/Catalogs/poiyomi-toon-9.3.64.json",

    [string]$ImporterPath = "XRENGINE/Scene/Importers/UnityMaterialImporter.cs"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-NormalizedRelativePath {
    param([string]$BasePath, [string]$Path)

    $baseFullPath = [IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $targetFullPath = [IO.Path]::GetFullPath($Path)
    $baseUri = [Uri]::new($baseFullPath)
    $targetUri = [Uri]::new($targetFullPath)
    [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('\', '/')
}

function Remove-SourceComments {
    param([string]$Text)

    $builder = [Text.StringBuilder]::new($Text.Length)
    $inString = $false
    $inLineComment = $false
    $inBlockComment = $false
    $escaped = $false

    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        $next = if ($index + 1 -lt $Text.Length) { $Text[$index + 1] } else { [char]0 }

        if ($inLineComment) {
            if ($character -eq "`n") {
                $inLineComment = $false
                [void]$builder.Append($character)
            }
            continue
        }

        if ($inBlockComment) {
            if ($character -eq '*' -and $next -eq '/') {
                $inBlockComment = $false
                $index++
            }
            elseif ($character -eq "`n") {
                [void]$builder.Append($character)
            }
            continue
        }

        if ($inString) {
            [void]$builder.Append($character)
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq '\') {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        if ($character -eq '/' -and $next -eq '/') {
            $inLineComment = $true
            $index++
            continue
        }

        if ($character -eq '/' -and $next -eq '*') {
            $inBlockComment = $true
            $index++
            continue
        }

        if ($character -eq '"') {
            $inString = $true
        }

        [void]$builder.Append($character)
    }

    $builder.ToString()
}

function Find-MatchingBrace {
    param([string]$Text, [int]$OpenBraceIndex)

    $depth = 0
    $inString = $false
    $escaped = $false

    for ($index = $OpenBraceIndex; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($inString) {
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq '\') {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        if ($character -eq '"') {
            $inString = $true
            continue
        }

        if ($character -eq '{') {
            $depth++
        }
        elseif ($character -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $index
            }
        }
    }

    throw "Unbalanced brace at source offset $OpenBraceIndex."
}

function Get-BracedBody {
    param([string]$Text, [string]$Keyword)

    $match = [regex]::Match($Text, "\b$([regex]::Escape($Keyword))\s*\{")
    if (-not $match.Success) {
        throw "Could not find '$Keyword' block."
    }

    $openBraceIndex = $Text.IndexOf('{', $match.Index)
    $closeBraceIndex = Find-MatchingBrace -Text $Text -OpenBraceIndex $openBraceIndex
    [pscustomobject]@{
        Start = $openBraceIndex + 1
        End = $closeBraceIndex
        Text = $Text.Substring($openBraceIndex + 1, $closeBraceIndex - $openBraceIndex - 1)
    }
}

function Split-PropertyDeclarations {
    param([string]$Text)

    $declarations = [Collections.Generic.List[object]]::new()
    $builder = [Text.StringBuilder]::new()
    $squareDepth = 0
    $parenthesisDepth = 0
    $braceDepth = 0
    $inString = $false
    $escaped = $false
    $seenEquals = $false
    $line = 1
    $declarationLine = 1

    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        [void]$builder.Append($character)

        if ($inString) {
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq '\') {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
        }
        else {
            switch ($character) {
                '"' { $inString = $true }
                '[' { $squareDepth++ }
                ']' { $squareDepth-- }
                '(' { $parenthesisDepth++ }
                ')' { $parenthesisDepth-- }
                '{' { $braceDepth++ }
                '}' { $braceDepth-- }
                '=' { $seenEquals = $true }
            }
        }

        if ($character -eq "`n") {
            if (-not $inString -and $seenEquals -and $squareDepth -eq 0 -and $parenthesisDepth -eq 0 -and $braceDepth -eq 0) {
                $value = $builder.ToString().Trim()
                if ($value.Length -gt 0) {
                    $declarations.Add([pscustomobject]@{ Text = $value; Line = $declarationLine })
                }
                [void]$builder.Clear()
                $seenEquals = $false
                $declarationLine = $line + 1
            }
            elseif ($builder.ToString().Trim().Length -eq 0) {
                [void]$builder.Clear()
                $declarationLine = $line + 1
            }
            $line++
        }
    }

    $remaining = $builder.ToString().Trim()
    if ($remaining.Length -gt 0) {
        $declarations.Add([pscustomobject]@{ Text = $remaining; Line = $declarationLine })
    }

    $declarations
}

function Parse-Attributes {
    param([string]$Text)

    $attributes = [Collections.Generic.List[object]]::new()
    foreach ($match in [regex]::Matches($Text, '\[(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\((?<arguments>.*?)\))?\]', [Text.RegularExpressions.RegexOptions]::Singleline)) {
        $attributes.Add([ordered]@{
            name = $match.Groups['name'].Value
            arguments = if ($match.Groups['arguments'].Success) { $match.Groups['arguments'].Value.Trim() } else { $null }
        })
    }
    $attributes
}

function Get-PropertyClassification {
    param(
        [string]$Name,
        [int]$RuntimeReferenceCount,
        [string[]]$AttributeNames,
        [string]$DisplayOptions
    )

    if ($Name -match '^(shader_|footer_|m_(start|end|main)|s_(start|end)|_ShaderUIWarning|GeometryShader_|Tessellation_|_ForgotToLockMaterial)') {
        return "internalData"
    }
    if ($Name -match '(?i)(AudioLink|LTCGI|VRC|Udon|LightVolume)') {
        return "integration"
    }
    if ($Name -match '^(?:_Mode|_Cull|_ZWrite|_ZTest|_Offset|_Fog|_ColorMask|_AlphaToMask|_AlphaToCoverage)' -or
        $Name -match '(?:Blend|BlendOp|Stencil|RenderQueue|RenderType)') {
        return "renderState"
    }
    if ($Name -match '(?:Optimizer|Lock|Animated|AnimSuffix)' -or $AttributeNames -contains 'ThryShaderOptimizerLockButton') {
        return "animationLocking"
    }
    if ($DisplayOptions -match '(?i)\balts\s*:') {
        return "compatibilityAlias"
    }
    if ($RuntimeReferenceCount -gt 0) {
        return "runtime"
    }
    return "inspectorOnly"
}

function Get-InitialParity {
    param(
        [string]$Classification,
        [bool]$CurrentlyMapped
    )

    if ($Classification -eq "runtime") {
        return $(if ($CurrentlyMapped) { "nativeEquivalent" } else { "missing" })
    }
    if ($Classification -eq "integration") {
        return "preservedInactive"
    }
    if ($Classification -in @("renderState", "animationLocking")) {
        return $(if ($CurrentlyMapped) { "nativeEquivalent" } else { "missing" })
    }
    return "notApplicable"
}

function Get-PassInventory {
    param([string]$Text)

    $passes = [Collections.Generic.List[object]]::new()
    foreach ($match in [regex]::Matches($Text, '(?m)^\s*Pass\s*\{')) {
        $openBraceIndex = $Text.IndexOf('{', $match.Index)
        $closeBraceIndex = Find-MatchingBrace -Text $Text -OpenBraceIndex $openBraceIndex
        $body = $Text.Substring($openBraceIndex + 1, $closeBraceIndex - $openBraceIndex - 1)
        $nameMatch = [regex]::Match($body, '(?m)^\s*Name\s+"(?<name>[^"]+)"')
        $tagsMatch = [regex]::Match($body, '(?m)^\s*Tags\s*\{(?<tags>[^}]*)\}')
        $states = [Collections.Generic.List[string]]::new()
        foreach ($stateMatch in [regex]::Matches($body, '(?m)^\s*(?<state>(?:Blend(?:Op)?|ZWrite|ZTest|Cull|Offset|ColorMask|AlphaToMask)\b[^\r\n]*|Stencil\s*\{[^}]*\})', [Text.RegularExpressions.RegexOptions]::Singleline)) {
            $states.Add(($stateMatch.Groups['state'].Value -replace '\s+', ' ').Trim())
        }
        $pragmas = [Collections.Generic.List[string]]::new()
        foreach ($pragmaMatch in [regex]::Matches($body, '(?m)^\s*#pragma\s+(?<pragma>[^\r\n]+)')) {
            $pragmas.Add($pragmaMatch.Groups['pragma'].Value.Trim())
        }
        $passes.Add([ordered]@{
            name = if ($nameMatch.Success) { $nameMatch.Groups['name'].Value } else { "<unnamed>" }
            tags = if ($tagsMatch.Success) { ($tagsMatch.Groups['tags'].Value -replace '\s+', ' ').Trim() } else { "" }
            states = @($states | Sort-Object -Unique)
            pragmas = @($pragmas | Sort-Object -Unique)
        })
    }
    $passes
}

function Get-AnnotationImplementation {
    param(
        [string]$Name,
        [IO.FileInfo[]]$SourceFiles,
        [string]$SourceRoot
    )

    $candidates = @(
        "${Name}Drawer",
        "${Name}Decorator",
        $Name
    )
    foreach ($file in $SourceFiles) {
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($candidate in $candidates) {
            if ($text -match "\bclass\s+$([regex]::Escape($candidate))\b") {
                return [ordered]@{
                    source = Get-NormalizedRelativePath -BasePath $SourceRoot -Path $file.FullName
                    symbol = $candidate
                    kind = "drawerOrDecorator"
                }
            }
        }
    }

    foreach ($file in $SourceFiles) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text -match "\b$([regex]::Escape($Name))\b") {
            return [ordered]@{
                source = Get-NormalizedRelativePath -BasePath $SourceRoot -Path $file.FullName
                symbol = $Name
                kind = "metadata"
            }
        }
    }

    $unityBuiltIns = @(
        "Enum",
        "Gamma",
        "HDR",
        "Header",
        "HideInInspector",
        "IntRange",
        "KeywordEnum",
        "MaterialToggle",
        "NonModifiableTextureData",
        "Normal",
        "NoScaleOffset",
        "PowerSlider",
        "Space",
        "Toggle",
        "ToggleUI"
    )
    if ($Name -in $unityBuiltIns) {
        return [ordered]@{
            source = "Unity ShaderLab built-in"
            symbol = $Name
            kind = "unityBuiltIn"
        }
    }

    [ordered]@{
        source = "No implementation in pinned Poiyomi/ThryEditor snapshot"
        symbol = $Name
        kind = "unresolved"
    }
}

function Get-WorkflowInventory {
    param([IO.FileInfo[]]$ThrySourceFiles, [string]$ThryRoot)

    $workflows = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($file in $ThrySourceFiles) {
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($match in [regex]::Matches($text, '\[\s*MenuItem\s*\(\s*"(?<path>[^"]+)"')) {
            $key = "menu:$($match.Groups['path'].Value)"
            if ($seen.Add($key)) {
                $line = 1 + ($text.Substring(0, $match.Index) -split "`n").Count - 1
                $workflows.Add([ordered]@{
                    id = $key
                    kind = "menu"
                    label = $match.Groups['path'].Value
                    source = "$(Get-NormalizedRelativePath -BasePath $ThryRoot -Path $file.FullName):$line"
                    currentXrengineSupport = "missing"
                    targetNativeBehavior = "Equivalent allowlisted ImGui/editor command"
                    classification = "nativeEquivalent"
                    owner = "Editor"
                    tests = @()
                })
            }
        }

        foreach ($match in [regex]::Matches($text, '\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?:EditorWindow|PopupWindowContent)\b')) {
            $key = "window:$($match.Groups['name'].Value)"
            if ($seen.Add($key)) {
                $line = 1 + ($text.Substring(0, $match.Index) -split "`n").Count - 1
                $workflows.Add([ordered]@{
                    id = $key
                    kind = "auxiliaryWindow"
                    label = $match.Groups['name'].Value
                    source = "$(Get-NormalizedRelativePath -BasePath $ThryRoot -Path $file.FullName):$line"
                    currentXrengineSupport = "missing"
                    targetNativeBehavior = "Native ImGui modal, popup, or tool window"
                    classification = "nativeEquivalent"
                    owner = "Editor"
                    tests = @()
                })
            }
        }
    }

    $requiredWorkflows = [ordered]@{
        inspectorHierarchy = "Editor/ThryEditor.cs"
        propertyContextMenu = "Editor/ThryEditor.cs"
        crossMaterialEditor = "Editor/CrossEditor.cs"
        materialPresets = "Editor/Presets.cs"
        materialLinking = "Editor/MaterialLinker.cs"
        shaderLocking = "Editor/ShaderOptimizer.cs"
        decalSceneTool = "Editor/DecalSceneTool.cs"
        texturePacker = "Editor/TexturePacker/Packer.cs"
        gradientEditor = "Editor/GradientEditor2.cs"
        textureUseLookup = "Editor/ListTextureUsesPopup.cs"
        localization = "Editor/Localization.cs"
        materialNotes = "Editor/UtilityWindows/SetNotePopup.cs"
        pasteSpecial = "Editor/UtilityWindows/PasteSpecialPopup.cs"
        unpreparedMaterialManager = "Editor/UnlockedMaterialList.cs"
        shaderTranslator = "Editor/Shader Translator/ShaderTranslator.cs"
        materialCleanup = "Editor/Helpers/MaterialCleaner.cs"
    }

    foreach ($entry in $requiredWorkflows.GetEnumerator()) {
        $sourcePath = Join-Path $ThryRoot $entry.Value
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required ThryEditor workflow source '$($entry.Value)' is missing."
        }
        $key = "workflow:$($entry.Key)"
        if ($seen.Add($key)) {
            $workflows.Add([ordered]@{
                id = $key
                kind = "inspectorWorkflow"
                label = $entry.Key
                source = $entry.Value
                currentXrengineSupport = "missing"
                targetNativeBehavior = "Native deterministic ImGui workflow with undo, dirty, and save integration"
                classification = "nativeEquivalent"
                owner = "Editor"
                tests = @()
            })
        }
    }

    @($workflows | Sort-Object { $_.id })
}

$resolvedRoot = (Resolve-Path -LiteralPath $PoiyomiRoot).Path
$shaderRelativePath = "_PoiyomiShaders/Shaders/9.3/Toon/Poiyomi Toon.shader"
$shaderMetaRelativePath = "$shaderRelativePath.meta"
$thryRelativePath = "_PoiyomiShaders/Scripts/ThryEditor"
$poiyomiEditorRelativePath = "_PoiyomiShaders/Scripts/Editor"
$shaderPath = Join-Path $resolvedRoot $shaderRelativePath
$thryRoot = Join-Path $resolvedRoot $thryRelativePath
$poiyomiEditorRoot = Join-Path $resolvedRoot $poiyomiEditorRelativePath
$packagePath = Join-Path $resolvedRoot "package.json"

foreach ($requiredPath in @($shaderPath, $thryRoot, $poiyomiEditorRoot, $packagePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required pinned source path '$requiredPath' does not exist."
    }
}

$commit = (& git -C $resolvedRoot rev-parse HEAD).Trim()
$thryTree = (& git -C $resolvedRoot rev-parse "HEAD:$thryRelativePath").Trim()
$shaderBlob = (& git -C $resolvedRoot rev-parse "HEAD:$shaderRelativePath").Trim()
$shaderMeta = (& git -C $resolvedRoot show "HEAD:$shaderMetaRelativePath") -join "`n"
$shaderGuidMatch = [regex]::Match($shaderMeta, '(?m)^guid:\s*(?<guid>[0-9a-f]{32})\s*$')
if (-not $shaderGuidMatch.Success) {
    throw "Pinned shader metadata does not contain a Unity GUID."
}

$package = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
if ($package.version -ne "9.3.64") {
    throw "Expected Poiyomi package 9.3.64 but pinned source reports '$($package.version)'."
}

$shaderText = [IO.File]::ReadAllText($shaderPath)
if ($shaderText -notmatch 'Poiyomi\s+9\.3\.64') {
    throw "Pinned shader does not contain the Poiyomi 9.3.64 identity marker."
}

$commentFreeShader = Remove-SourceComments -Text $shaderText
$propertiesBlock = Get-BracedBody -Text $commentFreeShader -Keyword "Properties"
$shaderBodyWithoutProperties = $commentFreeShader.Remove(
    $propertiesBlock.Start - 1,
    $propertiesBlock.End - $propertiesBlock.Start + 2)

$tokenCounts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($shaderBodyWithoutProperties, '\b[A-Za-z_][A-Za-z0-9_]*\b')) {
    $token = $match.Value
    $count = 0
    if ($tokenCounts.TryGetValue($token, [ref]$count)) {
        $tokenCounts[$token] = $count + 1
    }
    else {
        $tokenCounts[$token] = 1
    }
}

$importerText = if (Test-Path -LiteralPath $ImporterPath) { [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ImporterPath)) } else { "" }
$mappedProperties = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($importerText, '"(?<name>_[A-Za-z_][A-Za-z0-9_]*)"')) {
    [void]$mappedProperties.Add($match.Groups['name'].Value)
}

$properties = [Collections.Generic.List[object]]::new()
$annotationUsages = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
$displayOptionUsages = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
$actionTypeUsages = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
$propertyPattern = '^(?<attributes>(?:\s*\[[^\]]*\])*)\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*"(?<display>(?:\\.|[^"])*)"\s*,\s*(?<type>Range\s*\([^)]*\)|[A-Za-z0-9_]+)\s*\)\s*=\s*(?<default>.*)$'

foreach ($declaration in Split-PropertyDeclarations -Text $propertiesBlock.Text) {
    $match = [regex]::Match($declaration.Text, $propertyPattern, [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        throw "Could not parse active ShaderLab property declaration at property-block line $($declaration.Line): $($declaration.Text.Substring(0, [Math]::Min(160, $declaration.Text.Length)))"
    }

    $name = $match.Groups['name'].Value
    $attributes = @(Parse-Attributes -Text $match.Groups['attributes'].Value)
    $attributeNames = @($attributes | ForEach-Object { $_.name })
    foreach ($attributeName in $attributeNames) {
        $usageList = $null
        if (-not $annotationUsages.TryGetValue($attributeName, [ref]$usageList)) {
            $usageList = [Collections.Generic.List[string]]::new()
            $annotationUsages[$attributeName] = $usageList
        }
        $usageList.Add($name)
    }

    $display = $match.Groups['display'].Value
    $displayParts = $display -split '--', 2
    $displayOptions = if ($displayParts.Count -eq 2) { $displayParts[1].Trim() } else { "" }
    $optionKeys = @(
        [regex]::Matches($displayOptions, '(?<![A-Za-z0-9_])(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*:') |
            ForEach-Object { $_.Groups['key'].Value } |
            Where-Object { $_ -notin @("http", "https") } |
            Sort-Object -Unique
    )
    $actionTypes = @(
        [regex]::Matches($displayOptions, '(?i)\btype\s*:\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)') |
            ForEach-Object { $_.Groups['type'].Value.ToUpperInvariant() } |
            Sort-Object -Unique
    )
    foreach ($optionKey in $optionKeys) {
        $usageList = $null
        if (-not $displayOptionUsages.TryGetValue($optionKey, [ref]$usageList)) {
            $usageList = [Collections.Generic.List[string]]::new()
            $displayOptionUsages[$optionKey] = $usageList
        }
        $usageList.Add($name)
    }
    foreach ($actionType in $actionTypes) {
        $usageList = $null
        if (-not $actionTypeUsages.TryGetValue($actionType, [ref]$usageList)) {
            $usageList = [Collections.Generic.List[string]]::new()
            $actionTypeUsages[$actionType] = $usageList
        }
        $usageList.Add($name)
    }
    $references = @(
        [regex]::Matches($displayOptions, '(?<![A-Za-z0-9_])_[A-Za-z_][A-Za-z0-9_]*') |
            ForEach-Object Value |
            Sort-Object -Unique
    )
    $referenceCount = 0
    $runtimeReferenceCount = if ($tokenCounts.TryGetValue($name, [ref]$referenceCount)) { $referenceCount } else { 0 }
    $classification = Get-PropertyClassification -Name $name -RuntimeReferenceCount $runtimeReferenceCount -AttributeNames $attributeNames -DisplayOptions $displayOptions
    $currentlyMapped = $mappedProperties.Contains($name)
    $initialParity = Get-InitialParity -Classification $classification -CurrentlyMapped $currentlyMapped

    $properties.Add([ordered]@{
        name = $name
        sourceLine = $declaration.Line + ($commentFreeShader.Substring(0, $propertiesBlock.Start) -split "`n").Count - 1
        displayName = $displayParts[0]
        localizationKey = if (
            $displayParts[0].Trim().Length -gt 0 -and
            -not $name.StartsWith("footer_", [StringComparison]::Ordinal) -and
            $name -notin @("shader_master_label", "shader_locale", "shader_on_swap_to")
        ) { $name } else { $null }
        type = $match.Groups['type'].Value.Trim()
        defaultValue = ($match.Groups['default'].Value -replace '\s+', ' ').Trim()
        attributes = $attributes
        displayOptions = $displayOptions
        optionKeys = $optionKeys
        actionTypes = $actionTypes
        propertyReferences = $references
        runtimeReferenceCount = $runtimeReferenceCount
        classification = $classification
        initialParity = $initialParity
        currentXrengineSupport = if ($currentlyMapped) { "Mapped by UnityMaterialImporter" } else { "No semantic mapping" }
        targetNativeBehavior = switch ($initialParity) {
            "nativeEquivalent" { "Preserve authored semantics through the XRENGINE uber material contract" }
            "preservedInactive" { "Preserve source value and emit an unavailable-integration diagnostic" }
            "missing" { "Implement or explicitly reclassify before parity completion" }
            default { "No runtime conversion required" }
        }
        owner = switch ($classification) {
            "renderState" { "Rendering" }
            "animationLocking" { "Animation / Assets" }
            "inspectorOnly" { "Editor" }
            "internalData" { "Assets" }
            default { "Assets / Rendering" }
        }
        tests = @()
    })
}

$thrySourceFiles = @(Get-ChildItem -LiteralPath $thryRoot -Recurse -File -Filter "*.cs" | Sort-Object FullName)
$annotationSourceFiles = @(
    $thrySourceFiles
    Get-ChildItem -LiteralPath $poiyomiEditorRoot -Recurse -File -Filter "*.cs"
) | Sort-Object FullName
$annotations = [Collections.Generic.List[object]]::new()
foreach ($entry in $annotationUsages.GetEnumerator() | Sort-Object Key) {
    $implementation = Get-AnnotationImplementation -Name $entry.Key -SourceFiles $annotationSourceFiles -SourceRoot $resolvedRoot
    $annotations.Add([ordered]@{
        name = $entry.Key
        activeUsageCount = $entry.Value.Count
        properties = @($entry.Value | Sort-Object -Unique)
        source = $implementation.source
        symbol = $implementation.symbol
        implementationKind = $implementation.kind
        currentXrengineSupport = switch -Regex ($entry.Key) {
            '^(ToggleUI|Enum|KeywordEnum|PowerSlider|IntRange|NoScaleOffset|Normal|HDR|Gamma|Header|Space|HideInInspector)$' { "generic or native equivalent"; break }
            default { "missing specialized behavior" }
        }
        targetNativeBehavior = if ($implementation.kind -eq "unresolved") {
            "Preserve the unresolved annotation and report its unavailable source semantics"
        } else {
            "Data-driven ShaderAuthoringSchema annotation"
        }
        classification = if ($implementation.kind -eq "unresolved") { "unreachable" } else { "nativeEquivalent" }
        owner = "Editor"
        tests = @()
    })
}

$displayOptionCoverage = @(
    foreach ($entry in $displayOptionUsages.GetEnumerator() | Sort-Object Key) {
        [ordered]@{
            name = $entry.Key
            activeUsageCount = $entry.Value.Count
            properties = @($entry.Value | Sort-Object -Unique)
            source = "$thryRelativePath/Editor/DataStructs/PropertyOptions.cs"
            currentXrengineSupport = "missing specialized behavior"
            targetNativeBehavior = "Typed ShaderAuthoringSchema display option"
            classification = "nativeEquivalent"
            owner = "Editor"
            tests = @()
        }
    }
)

$actionTypeCoverage = @(
    foreach ($entry in $actionTypeUsages.GetEnumerator() | Sort-Object Key) {
        [ordered]@{
            name = $entry.Key
            activeUsageCount = $entry.Value.Count
            properties = @($entry.Value | Sort-Object -Unique)
            source = "$thryRelativePath/Editor/DataStructs/DefineableAction.cs"
            currentXrengineSupport = "missing typed action"
            targetNativeBehavior = "Allowlisted transactional MaterialInspectorActionGraph action"
            classification = "nativeEquivalent"
            owner = "Editor"
            tests = @()
        }
    }
)

$keywordGroups = [Collections.Generic.List[object]]::new()
foreach ($match in [regex]::Matches($commentFreeShader, '(?m)^\s*#pragma\s+(?<kind>shader_feature(?:_local)?|multi_compile(?:_local)?)\s+(?<values>[^\r\n]+)')) {
    $keywordGroups.Add([ordered]@{
        kind = $match.Groups['kind'].Value
        values = @(($match.Groups['values'].Value -split '\s+') | Where-Object { $_ } | Sort-Object -Unique)
    })
}

$featureSymbols = @(
    [regex]::Matches($commentFreeShader, '(?m)^\s*#\s*(?:ifdef|ifndef)\s+(?<symbol>[A-Za-z_][A-Za-z0-9_]*)|defined\s*\(\s*(?<symbol>[A-Za-z_][A-Za-z0-9_]*)\s*\)') |
        ForEach-Object { $_.Groups['symbol'].Value } |
        Where-Object { $_ } |
        Sort-Object -Unique
)

$diagnosticCodes = @(
    [ordered]@{ code = "POI0001"; severity = "warning"; meaning = "Poiyomi shader family recognized but source version is unknown." }
    [ordered]@{ code = "POI0002"; severity = "warning"; meaning = "Locked/generated shader matched only by property signature; original GUID or marker was unavailable." }
    [ordered]@{ code = "POI0003"; severity = "error"; meaning = "Runtime-visible source property has no parity classification." }
    [ordered]@{ code = "POI0004"; severity = "error"; meaning = "Pinned source identity or generated catalog hash does not match." }
    [ordered]@{ code = "POI0005"; severity = "warning"; meaning = "Source feature is preserved but inactive because an engine integration is unavailable." }
    [ordered]@{ code = "POI0006"; severity = "warning"; meaning = "Source property is recognized but its runtime conversion is not implemented." }
    [ordered]@{ code = "POI0007"; severity = "error"; meaning = "Source property could not be parsed or preserved." }
    [ordered]@{ code = "POI0008"; severity = "warning"; meaning = "Source render state has no exact XRENGINE representation." }
    [ordered]@{ code = "POI0009"; severity = "warning"; meaning = "Source animation binding could not be mapped deterministically." }
    [ordered]@{ code = "POI0010"; severity = "info"; meaning = "Native equivalent differs intentionally from Unity, VRChat, or ThryEditor behavior." }
)

$catalog = [ordered]@{
    schemaVersion = 1
    generator = "Tools/Reports/Generate-PoiyomiToon93Catalog.ps1"
    source = [ordered]@{
        shaderFamily = "Poiyomi Toon"
        shaderVersion = $package.version
        shaderName = ".poiyomi/Poiyomi Toon"
        repositoryUrl = "https://github.com/poiyomi/PoiyomiToonShader"
        sourceUrl = "https://raw.githubusercontent.com/poiyomi/PoiyomiToonShader/$commit/$($shaderRelativePath.Replace(' ', '%20'))"
        commit = $commit
        commitDate = (& git -C $resolvedRoot log -1 --format=%cI).Trim()
        shaderPath = $shaderRelativePath
        shaderGuid = $shaderGuidMatch.Groups['guid'].Value
        shaderBlob = $shaderBlob
        shaderSha256 = (Get-FileHash -LiteralPath $shaderPath -Algorithm SHA256).Hash.ToLowerInvariant()
        thryEditorPath = $thryRelativePath
        thryEditorTree = $thryTree
        license = "MIT"
        licenseCopyright = "Copyright (c) 2023 Poiyomi Inc."
        thryEditorLicense = "MIT"
        thryEditorLicenseCopyright = "Copyright (c) 2022 Thryrallo"
    }
    matchingPolicy = [ordered]@{
        acceptedVersion = "9.3.64"
        acceptedShaderGuid = $shaderGuidMatch.Groups['guid'].Value
        unlockedEvidence = @("exact shader GUID", "exact version marker plus canonical shader name", "canonical 9.3/Toon path plus exact version marker")
        lockedEvidence = @("OriginalShaderGUID override tag", "Hidden/Locked shader name plus exact source marker", "required property signature plus optimizer marker")
        requiredPropertySignature = @("shader_master_label", "shader_is_using_thry_editor", "_ShaderOptimizerEnabled", "_MainTex", "_ShadingEnabled")
        ambiguousPropertySignatureBehavior = "warn with POI0002"
        unknownVersionBehavior = "reject Poiyomi conversion and warn with POI0001"
    }
    namingPolicy = [ordered]@{
        material = "{sourceAssetStem}.poiyomi-9_3_64.uber"
        passVariant = "{materialName}.{passRole}.{variantHash:x16}"
        preservedMetadata = "{materialName}.poiyomi-source.json"
        animationBinding = "{materialName}/{semanticProperty}"
        collisionSuffix = ".{sourceGuid8}"
    }
    diagnostics = $diagnosticCodes
    summary = [ordered]@{
        propertyCount = $properties.Count
        texturePropertyCount = @($properties | Where-Object { $_.type -match '^(?:2D|2DArray|3D|Cube|CubeArray)$' }).Count
        passCount = @(Get-PassInventory -Text $commentFreeShader).Count
        annotationKindCount = $annotations.Count
        activeAnnotationUsageCount = ($annotations | ForEach-Object { $_.activeUsageCount } | Measure-Object -Sum).Sum
        displayOptionKindCount = $displayOptionCoverage.Count
        activeDisplayOptionUsageCount = ($displayOptionCoverage | ForEach-Object { $_.activeUsageCount } | Measure-Object -Sum).Sum
        actionTypeCount = $actionTypeCoverage.Count
        localizationKeyCount = @($properties | Where-Object { $_.localizationKey }).Count
        workflowCount = @(Get-WorkflowInventory -ThrySourceFiles $thrySourceFiles -ThryRoot $thryRoot).Count
        unclassifiedRuntimePropertyCount = @($properties | Where-Object { $_.classification -eq "runtime" -and $_.initialParity -notin @("nativeEquivalent", "preservedInactive", "missing", "exact") }).Count
    }
    passes = @(Get-PassInventory -Text $commentFreeShader)
    keywordGroups = @($keywordGroups | Sort-Object { "$($_.kind):$($_.values -join ',')" } -Unique)
    featureSymbols = $featureSymbols
    properties = @($properties)
    annotations = @($annotations)
    displayOptions = $displayOptionCoverage
    actionTypes = $actionTypeCoverage
    workflows = @(Get-WorkflowInventory -ThrySourceFiles $thrySourceFiles -ThryRoot $thryRoot)
    fixturePolicy = [ordered]@{
        repositoryFixtures = "Only original XRENGINE-authored minimal Unity YAML fixtures are checked in during Phase 0."
        upstreamShader = "Not redistributed; generator consumes a user-provided checkout at the pinned commit."
        upstreamLicenses = @(
            [ordered]@{ component = "Poiyomi Toon Shader"; license = "MIT"; copyright = "Copyright (c) 2023 Poiyomi Inc." }
            [ordered]@{ component = "Embedded ThryEditor"; license = "MIT"; copyright = "Copyright (c) 2022 Thryrallo" }
        )
    }
}

if ($catalog.summary.unclassifiedRuntimePropertyCount -ne 0) {
    throw "Generated catalog contains $($catalog.summary.unclassifiedRuntimePropertyCount) unclassified runtime properties."
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$json = $catalog | ConvertTo-Json -Depth 100 -Compress
[IO.File]::WriteAllText($resolvedOutput, $json.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Generated $resolvedOutput"
Write-Output "Properties: $($catalog.summary.propertyCount); passes: $($catalog.summary.passCount); annotations: $($catalog.summary.annotationKindCount); workflows: $($catalog.summary.workflowCount)"
