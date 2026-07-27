[CmdletBinding()]
param(
    [string] $CatalogPath = "XREngine.Editor/Importers/Poiyomi/Catalogs/poiyomi-toon-9.3.64.json",
    [string] $WidgetRegistryPath = "XREngine.Editor/MaterialAuthoring/ShaderAuthoringWidgetRegistry.cs",
    [string] $OutputPath = "docs/reference/rendering/poiyomi-toon-9.3.64-parity.md"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Escape-Cell([string] $Value) {
    if ($null -eq $Value) { return "" }
    $Value.Replace("|", "\|").Replace("`r", " ").Replace("`n", " ")
}

function Get-WorkflowParity([string] $Id, [string] $Kind) {
    if ($Id -match '(?i)Dev Test|Ifex Indenting') { return @('Developer only', 'Existing engine test/developer tooling') }
    if ($Id -match '(?i)Twitter') { return @('Preserved inactive', 'Unsafe social action is inert text') }
    if ($Id -match '(?i)Copy GUID') { return @('Native', 'Copy stable XRENGINE asset identity') }
    if ($Id -match '(?i)TextureArray|Flipbooks') { return @('Native', 'Versioned texture-array recipe workspace') }
    if ($Id -match '(?i)Cross') { return @('Native', 'Semantic cross-shader material editor') }
    if ($Id -match '(?i)Cleaner|Cleanup') { return @('Native', 'Protected material-cleanup report') }
    if ($Id -match '(?i)Lock|Unlock|unprepared') { return @('Native', 'Optimize/prepare variant manager') }
    if ($Id -match '(?i)Locale|localization|Settings') { return @('Native', 'Versioned locale/preferences workspace') }
    if ($Id -match '(?i)Translator|Translate') { return @('Native', 'Semantic shader conversion preview') }
    if ($Id -match '(?i)Texture|Packer') { return @('Native', 'Texture packer, usage, and array workspace') }
    if ($Id -match '(?i)Decal') { return @('Native', 'Viewport decal controller') }
    if ($Id -match '(?i)Gradient') { return @('Native', 'Gradient and curve workspace') }
    if ($Id -match '(?i)Link') { return @('Native', 'Cycle-safe semantic material links') }
    if ($Id -match '(?i)Note|TextPopup') { return @('Native', 'Persistent local notes') }
    if ($Id -match '(?i)Preset') { return @('Native', 'Versioned preset library and preview') }
    if ($Id -match '(?i)Paste') { return @('Native', 'Versioned hierarchical Paste Special') }
    if ($Id -match '(?i)SearchableEnum') { return @('Native', 'Typed searchable enum widget') }
    if ($Id -match '(?i)Keywords|Animated Properties') { return @('Native', 'Variant and animation semantic repair') }
    if ($Id -match '(?i)propertyContextMenu|inspectorHierarchy') { return @('Native', 'Schema-driven inspector interaction') }
    if ($Kind -eq 'auxiliaryWindow') { return @('Native', 'Native ImGui authoring workspace') }
    @('Preserved inactive', 'Reviewed source workflow retained without execution')
}

$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
$registrySource = Get-Content -LiteralPath $WidgetRegistryPath -Raw
$registered = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($match in [regex]::Matches($registrySource, '\["(?<id>[^"]+)"\]\s*=\s*new')) {
    [void]$registered.Add($match.Groups['id'].Value)
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Poiyomi Toon 9.3.64 Parity')
$lines.Add('')
$lines.Add('This report is generated from the pinned source catalog and the engine-owned widget registry. Do not edit it by hand.')
$lines.Add('')
$lines.Add("- Source commit: ``$($catalog.source.commit)``")
$lines.Add("- Shader SHA-256: ``$($catalog.source.shaderSha256)``")
$lines.Add("- Properties: $($catalog.summary.propertyCount)")
$lines.Add("- Passes: $($catalog.summary.passCount)")
$lines.Add("- Active annotation kinds: $($catalog.summary.annotationKindCount)")
$lines.Add("- Reachable workflows: $($catalog.summary.workflowCount)")
$lines.Add('')
$lines.Add('## Property Support Summary')
$lines.Add('')
$lines.Add('| Catalog state | Runtime support statement | Count |')
$lines.Add('| --- | --- | ---: |')
foreach ($group in $catalog.properties | Group-Object classification, initialParity | Sort-Object Name) {
    $sample = $group.Group[0]
    $support = switch ([string]$sample.initialParity) {
        'nativeEquivalent' { 'Exact or reviewed native mapping' }
        'preservedInactive' { 'Preserved inactive; unavailable integration is reported' }
        'missing' { 'Preserved inactive; `POI0006` reports the absent runtime mapping' }
        default { 'Catalog/editor data; no runtime conversion required' }
    }
    $lines.Add("| $(Escape-Cell "$($sample.classification) / $($sample.initialParity)") | $support | $($group.Count) |")
}
$lines.Add('')
$lines.Add('Every runtime-visible source value is retained in the versioned descriptor even when its runtime mapping is inactive.')
$lines.Add('')
$lines.Add('## Active Annotation Parity')
$lines.Add('')
$lines.Add('| Annotation | Active uses | Classification | XRENGINE equivalent |')
$lines.Add('| --- | ---: | --- | --- |')
foreach ($annotation in $catalog.annotations | Sort-Object name) {
    if ($annotation.activeUsageCount -le 0) { continue }
    $native = $registered.Contains([string]$annotation.name)
    $classification = if ($native) { 'Native' } else { 'Preserved inactive' }
    $equivalent = if ($native) {
        'Typed `ShaderAuthoringWidgetRegistry` capability'
    } else {
        'Visible unsupported node; metadata remains inert'
    }
    $lines.Add("| ``$(Escape-Cell $annotation.name)`` | $($annotation.activeUsageCount) | $classification | $equivalent |")
}
$lines.Add('')
$lines.Add('## Reachable Workflow Parity')
$lines.Add('')
$lines.Add('| Workflow | Kind | Classification | XRENGINE equivalent |')
$lines.Add('| --- | --- | --- | --- |')
foreach ($workflow in $catalog.workflows | Sort-Object id) {
    $parity = Get-WorkflowParity ([string]$workflow.id) ([string]$workflow.kind)
    $lines.Add("| ``$(Escape-Cell $workflow.id)`` | $(Escape-Cell $workflow.kind) | $($parity[0]) | $(Escape-Cell $parity[1]) |")
}
$lines.Add('')
$lines.Add('## Review Contract')
$lines.Add('')
$lines.Add('- Native entries are exercised by schema, widget, interaction, undo, persistence, and security tests.')
$lines.Add('- Preserved-inactive entries never execute arbitrary code, reflection, remote fetches, or external commands.')
$lines.Add('- A source update must regenerate this report and include the reviewed diff with updated fixtures.')

$resolved = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolved)) | Out-Null
[IO.File]::WriteAllText($resolved, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Generated $resolved"
