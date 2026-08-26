param(
    [string]$OutputPath = "docs/work/progress/runtime/runtime-modularization-phase6-source-ownership.tsv"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$facadeRoot = Join-Path $repositoryRoot "XRENGINE"
$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    Join-Path $repositoryRoot $OutputPath
}

$existingRowsBySource = @{}
if (Test-Path -LiteralPath $resolvedOutputPath) {
    $existingLines = [System.IO.File]::ReadAllLines($resolvedOutputPath)
    if ($existingLines.Count -gt 1) {
        $existingHeaders = $existingLines[0].Split("`t", [System.StringSplitOptions]::None)
        foreach ($line in $existingLines[1..($existingLines.Count - 1)]) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $values = $line.Split("`t", [System.StringSplitOptions]::None)
            $row = @{}
            for ($index = 0; $index -lt $existingHeaders.Count; $index++) {
                $row[$existingHeaders[$index]] = if ($index -lt $values.Count) { $values[$index] } else { "" }
            }

            $sourcePath = ([string]$row.SourcePath).Replace("\", "/")
            if (-not [string]::IsNullOrWhiteSpace($sourcePath)) {
                $existingRowsBySource[$sourcePath] = $row
            }
        }
    }
}

# Keep the checked-in baseline as an immutable source ledger even after `git mv`
# removes completed facade paths from the index. The working manifest wins when
# it already contains a row with newer migration state.
$baselineManifestPath = "docs/work/progress/runtime/runtime-modularization-phase6-source-ownership.tsv"
$baselineLines = @(& git -C $repositoryRoot show "HEAD:$baselineManifestPath" 2>$null)
if ($LASTEXITCODE -eq 0 -and $baselineLines.Count -gt 1) {
    $baselineHeaders = $baselineLines[0].Split("`t", [System.StringSplitOptions]::None)
    foreach ($line in $baselineLines[1..($baselineLines.Count - 1)]) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $values = $line.Split("`t", [System.StringSplitOptions]::None)
        $row = @{}
        for ($index = 0; $index -lt $baselineHeaders.Count; $index++) {
            $row[$baselineHeaders[$index]] = if ($index -lt $values.Count) { $values[$index] } else { "" }
        }

        $sourcePath = ([string]$row.SourcePath).Replace("\", "/")
        if (-not [string]::IsNullOrWhiteSpace($sourcePath) -and
            -not $existingRowsBySource.ContainsKey($sourcePath)) {
            $existingRowsBySource[$sourcePath] = $row
        }
    }
}

# Completed ownership moves are declared here so regenerating the manifest cannot erase
# migration state after the tracked facade source has been removed. Every destination is a
# real owner path and is validated before a Pending row is promoted to Migrated.
$completedMigrationDestinations = @{
    "XRENGINE/Core/Engine/AnimationClipSerialization.cs" = "XREngine.Animation/Serialization/AnimationClipSerialization.cs"
    "XRENGINE/Core/Engine/AnimationCurveYamlModel.cs" = "XREngine.Animation/Serialization/AnimationCurveYamlModel.cs"
    "XRENGINE/Core/Engine/AnimationCurveYamlTypeConverter.cs" = "XREngine.Animation/Serialization/AnimationCurveYamlTypeConverter.cs"
    "XRENGINE/Core/Engine/AnimationPropertySerialization.cs" = "XREngine.Animation/Serialization/AnimationPropertySerialization.cs"
    "XRENGINE/Core/Engine/AnimStateMachineSerialization.cs" = "XREngine.Animation/Serialization/AnimStateMachineSerialization.cs"
    "XRENGINE/Core/Engine/AssetManager.cs" = "XREngine.Runtime.Core/Assets/AssetManager.cs"
    "XRENGINE/Core/Engine/AssetManager.FileWatching.cs" = "XREngine.Runtime.Core/Assets/AssetManager.FileWatching.cs"
    "XRENGINE/Core/Engine/AssetManager.Metadata.cs" = "XREngine.Runtime.Core/Assets/AssetManager.Metadata.cs"
    "XRENGINE/Core/Engine/AssetManager.Published.cs" = "XREngine.Runtime.Core/Assets/AssetManager.Published.cs"
    "XRENGINE/Core/Engine/AssetManager.RenderAssetSerializationServices.cs" = "XREngine.Runtime.Bootstrap/Assets/RuntimeAssetBootstrap.cs"
    "XRENGINE/Core/Engine/AssetManager.Saving.cs" = "XREngine.Runtime.Core/Assets/AssetManager.Saving.cs"
    "XRENGINE/Core/Engine/AssetManager.Serialization.cs" = "XREngine.Runtime.Core/Assets/AssetManager.Serialization.cs"
    "XRENGINE/Core/Engine/AssetManagerRenderAssetSerializationServices.cs" = "XREngine.Runtime.Core/Serialization/AssetManagerAssetSerializationServices.cs"
    "XRENGINE/Core/Engine/AssetMetadata.cs" = "XREngine.Data/Serialization/Yaml/AssetMetadata.cs"
    "XRENGINE/Core/Engine/BlendTreeSerialization.cs" = "XREngine.Animation/Serialization/BlendTreeSerialization.cs"
    "XRENGINE/Core/Engine/InterfaceCollectionYamlNodeDeserializer.cs" = "XREngine.Data/Serialization/Yaml/InterfaceCollectionYamlNodeDeserializer.cs"
    "XRENGINE/Core/Engine/Loading/AssetManager.Loading.Core.cs" = "XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Core.cs"
    "XRENGINE/Core/Engine/Loading/AssetManager.Loading.Remote.cs" = "XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Api.Core.cs"
    "XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs" = "XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Core.cs;XREngine.Animation/Serialization/AnimationClipBinaryCacheCodec.cs;XREngine.Runtime.Rendering/Serialization/TextureStreamingCacheCodec.cs;XREngine.Runtime.ModelingBridge/Importing/Caching/ModelCachePathPolicy.cs"
    "XRENGINE/Core/Engine/Loading/AssetManager.Loading.ThirdParty.cs" = "XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Api.Core.cs;XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Core.cs;XREngine.Runtime.Core/Assets/RuntimeThirdPartyAssetLoadingServices.cs"
    "XRENGINE/Core/Engine/ModelCaching/ModelBinaryCacheCodec.cs" = "XREngine.Runtime.ModelingBridge/Importing/Caching/ModelBinaryCacheCodec.cs"
    "XRENGINE/Core/Engine/MotionSerialization.cs" = "XREngine.Animation/Serialization/MotionSerialization.cs"
    "XRENGINE/Core/Engine/PolymorphicTypeGraphVisitor.cs" = "XREngine.Data/Serialization/Yaml/PolymorphicTypeGraphVisitor.cs"
    "XRENGINE/Core/Engine/PolymorphicYamlNodeDeserializer.cs" = "XREngine.Data/Serialization/Yaml/PolymorphicYamlNodeDeserializer.cs"
    "XRENGINE/Core/Engine/SerializedAssetSupport.cs" = "XREngine.Data/Serialization/SerializedAssetSupport.cs"
    "XRENGINE/Core/Engine/TextFileYamlTypeConverter.cs" = "XREngine.Data/Serialization/Yaml/TextFileYamlTypeConverter.cs"
    "XRENGINE/Core/Engine/TransformBaseYamlTypeConverter.cs" = "XREngine.Runtime.Core/Serialization/TransformBaseYamlTypeConverter.cs"
    "XRENGINE/Core/Engine/XRAssetYamlTypeConverter.cs" = "XREngine.Runtime.Core/Serialization/XRAssetYamlTypeConverter.cs;XREngine.Data/Serialization/Yaml/YamlEnumAliasRegistry.cs"
    "XRENGINE/Core/Engine/YamlDefaultTypeInspector.cs" = "XREngine.Data/Serialization/Yaml/YamlDefaultTypeInspector.cs"
    "XRENGINE/Core/Files/AssetPacker/ArchiveEntryInfo.cs" = "XREngine.Data/Core/Files/AssetPacker/ArchiveEntryInfo.cs"
    "XRENGINE/Core/Files/AssetPacker/ArchiveInfo.cs" = "XREngine.Data/Core/Files/AssetPacker/ArchiveInfo.cs"
    "XRENGINE/Core/Files/AssetPacker/AssetPacker.cs" = "XREngine.Data/Core/Files/AssetPacker/AssetPacker.cs"
    "XRENGINE/Core/Files/AssetPacker/FooterInfo.cs" = "XREngine.Data/Core/Files/AssetPacker/FooterInfo.cs"
    "XRENGINE/Core/Files/AssetPacker/StringCompressor.cs" = "XREngine.Data/Core/Files/AssetPacker/StringCompressor.cs"
    "XRENGINE/Core/Files/AssetPacker/TocEntryData.cs" = "XREngine.Data/Core/Files/AssetPacker/TocEntryData.cs"
    "XRENGINE/Core/Files/CookedAssetBlob.cs" = "XREngine.Data/Core/Files/CookedAssetBlob.cs"
    "XRENGINE/Core/Files/CookedAssetTypeReference.cs" = "XREngine.Data/Core/Files/CookedAssetTypeReference.cs"
    "XRENGINE/Core/Files/CookedBinary/CookedBinarySerializer.Schema.cs" = "XREngine.Data/Core/Files/CookedBinary/CookedBinarySerializer.Schema.cs"
    "XRENGINE/Core/Files/CookedBinary/CookedBinarySerializer.cs" = "XREngine.Data/Core/Files/CookedBinary/CookedBinarySerializer.cs"
    "XRENGINE/Core/Files/CookedBinary/CookedBinaryTypeMarker.cs" = "XREngine.Data/Core/Files/CookedBinary/CookedBinaryTypeMarker.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Collections/Array.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Collections/Array.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Collections/Dictionary.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Collections/Dictionary.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Collections/HashSet.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Collections/HashSet.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Collections/List.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Collections/List.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/ByteArray.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/ByteArray.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/DataSource.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/DataSource.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/Enum.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/Enum.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/Nullable.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/Nullable.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/Primitive.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/Primitive.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/Registry.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/Registry.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/TypeReference.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/TypeReference.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/ValueTuple.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/ValueTuple.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Core/XREvent.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Core/XREvent.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Custom/AnimationClip.cs" = "XREngine.Animation/Serialization/CookedBinary/AnimationClipCookedBinaryCodec.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Custom/AnimStateMachine.cs" = "XREngine.Animation/Serialization/CookedBinary/AnimStateMachineCookedBinaryCodec.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Custom/BlendTree.cs" = "XREngine.Animation/Serialization/CookedBinary/BlendTreeCookedBinaryCodec.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Custom/BlittableStruct.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Custom/BlittableStruct.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Custom/CustomSerializable.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Custom/CustomSerializable.cs"
    "XRENGINE/Core/Files/CookedBinary/Modules/Custom/Object.cs" = "XREngine.Data/Core/Files/CookedBinary/Modules/Custom/Object.cs"
    "XRENGINE/Core/Files/DirectStorageIO.cs" = "XREngine.Runtime.Core/Assets/IO/DirectStorageIO.cs"
    "XRENGINE/Core/Files/EventArgs.cs" = "XREngine.Data/Core/Files/EventArgs.cs"
    "XRENGINE/Core/Files/EventRaisingStreamWriter.cs" = "XREngine.Data/Core/Files/EventRaisingStreamWriter.cs"
    "XRENGINE/Core/Files/FileMap.cs" = "XREngine.Data/Core/Files/FileMap.cs"
    "XRENGINE/Core/Files/PublishedCookedAssetRegistryRegistration.cs" = "XREngine.Data/Core/Files/DataPublishedCookedAssetRegistration.cs;XREngine.Animation/Serialization/AnimationPublishedCookedAssetRegistration.cs;XREngine.Runtime.Rendering/Core/Files/PublishedCookedAssetRegistryRegistration.cs"
    "XRENGINE/Core/Files/TocLookupMode.cs" = "XREngine.Data/Core/Files/TocLookupMode.cs"
    "XRENGINE/Core/Files/XRAsset.MemoryPack.cs" = "XREngine.Data/Core/Files/XRAsset.MemoryPack.cs"
    "XRENGINE/Core/Files/XRProject.cs" = "XREngine.Runtime.Core/Assets/XRProject.cs"
    "XRENGINE/Settings/SecretCipher.cs" = "XREngine.Data/Settings/ISecretCipherServices.cs;XREngine.Data/Settings/SecretCipherServices.cs;XREngine.Editor/Settings/EditorSecretCipherServices.cs"
}

# P6.2 preserves the original facade paths in the ledger while promoting the
# completed runtime-core, networking, and physics moves to their concrete owners.
$completedMigrationDestinations["XRENGINE/Core/FrameEventArgs.cs"] = "XREngine.Runtime.Core/Core/FrameEventArgs.cs"
$completedMigrationDestinations["XRENGINE/Core/Platform/Linux.cs"] = "XREngine.Runtime.Core/Core/Platform/Linux.cs"
$completedMigrationDestinations["XRENGINE/Core/Platform/OSX.cs"] = "XREngine.Runtime.Core/Core/Platform/OSX.cs"
$completedMigrationDestinations["XRENGINE/Core/PlayModeConfiguration.cs"] = "XREngine.Runtime.Core/Core/PlayModeConfiguration.cs"
$completedMigrationDestinations["XRENGINE/Core/Time/CollectVisibleGenerationGate.cs"] = "XREngine.Runtime.Core/Core/Time/CollectVisibleGenerationGate.cs"
$completedMigrationDestinations["XRENGINE/Engine/Networking/Engine.BaseNetworkingManager.cs"] = "XREngine.Runtime.Core/Networking/BaseNetworkingManager.cs"
$completedMigrationDestinations["XRENGINE/Engine/Networking/Engine.ClientNetworkingManager.cs"] = "XREngine.Runtime.Core/Networking/ClientNetworkingManager.cs"
$completedMigrationDestinations["XRENGINE/Engine/Networking/Engine.ServerNetworkingManager.cs"] = "XREngine.Runtime.Core/Networking/ServerNetworkingManager.cs"
$completedMigrationDestinations["XRENGINE/Engine/Networking/RemoteJobNetworkingTransport.cs"] = "XREngine.Runtime.Core/Networking/RemoteJobNetworkingTransport.cs"
$completedMigrationDestinations["XRENGINE/Engine/Networking/ServerSessionResolver.cs"] = "XREngine.Runtime.Core/Networking/ServerSessionResolver.cs"
$completedMigrationDestinations["XRENGINE/Engine/Networking/WorldAssetIdentityProvider.cs"] = "XREngine.Runtime.Core/Networking/WorldAssetIdentityProvider.cs"

# P6.3 removes the world facade after splitting its responsibilities across the
# canonical Core identity and focused rendering, input, bootstrap, and editor owners.
$completedMigrationDestinations["XRENGINE/Rendering/XRWorldInstance.cs"] = "XREngine.Runtime.Core/World/RuntimeWorld.cs;XREngine.Runtime.Core/World/RuntimeWorld.Transforms.cs;XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs;XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.Picking.cs;XREngine.Runtime.InputIntegration/Input/RuntimeWorldInputIntegration.cs;XREngine.Runtime.Bootstrap/WorldHost/RuntimeWorldHost.cs;XREngine.Editor/World/EditorWorldIntegration.cs"
$completedMigrationDestinations["XRENGINE/Rendering/XRWorldInstance.PhysicsDebug.cs"] = "XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs;XREngine.Runtime.Bootstrap/WorldHost/RuntimeWorldHost.cs"
$completedMigrationDestinations["XRENGINE/Rendering/XRWorldInstance.PhysicsRaycastRequest.cs"] = "XREngine.Runtime.Core/World/RuntimeWorld.cs;XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.Picking.cs"

foreach ($sourcePath in $existingRowsBySource.Keys) {
    $physicsPrefix = "XRENGINE/Scene/Components/Physics/"
    if (-not $sourcePath.StartsWith($physicsPrefix, [System.StringComparison]::Ordinal)) {
        continue
    }

    $relativePhysicsPath = $sourcePath.Substring($physicsPrefix.Length)
    $destinationPath = if ($relativePhysicsPath -eq "PhysicsChainComponent.RenderingCompute.cs") {
        "XREngine.Runtime.Core/Scene/Components/Physics/IRuntimePhysicsChainRenderingBridge.cs;XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs"
    }
    elseif ($relativePhysicsPath -eq "GPUSoftbodyComponent.cs" -or
            $relativePhysicsPath.StartsWith("Joints/", [System.StringComparison]::Ordinal)) {
        "XREngine.Runtime.Rendering/Scene/Components/Physics/$relativePhysicsPath"
    }
    else {
        "XREngine.Runtime.Core/Scene/Components/Physics/$relativePhysicsPath"
    }

    $completedMigrationDestinations[$sourcePath] = $destinationPath
}

$completedDeletions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
[void]$completedDeletions.Add("XRENGINE/GlobalUsings.Physics.cs")
[void]$completedDeletions.Add("XRENGINE/GlobalWorldTypeAliases.cs")

function New-OwnershipDecision {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Move", "Split", "Delete", "Refactor")]
        [string]$Disposition,

        [Parameter(Mandatory)]
        [string[]]$FinalOwners,

        [Parameter(Mandatory)]
        [string]$Rationale
    )

    [pscustomobject]@{
        Disposition = $Disposition
        FinalOwners = $FinalOwners
        Rationale = $Rationale
    }
}

function Get-OwnershipDecision {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    $path = $RelativePath.Replace("\", "/")

    if ($path -eq "XRENGINE/GlobalUsings.Physics.cs") {
        return New-OwnershipDecision Delete "Removed" "Remove the facade-only global physics import after callers use destination-owned namespaces."
    }

    if ($path -eq "XRENGINE/GlobalWorldTypeAliases.cs") {
        return New-OwnershipDecision Delete "Removed" "Remove facade-only world aliases after consumers reference the canonical Runtime.Core and Runtime.Rendering contracts."
    }

    if ($path -like "XRENGINE/Properties/*") {
        return New-OwnershipDecision Delete "Removed" "Assembly metadata and type forwards exist only for the legacy XREngine.dll identity and are removed after repository identity migration."
    }

    if ($path -like "XRENGINE/Settings/Editor*" -or $path -like "XRENGINE/Settings/Secret*") {
        return New-OwnershipDecision Move "XREngine.Editor" "Editor preferences, overrides, environment policy, and encrypted secrets are editor application concerns."
    }

    if ($path -like "XRENGINE/Settings/Game*StartupSettings.cs") {
        return New-OwnershipDecision Split @("XREngine.Data", "XREngine.Runtime.Bootstrap") "Keep stable startup values in Data and move normalization/composition policy to Bootstrap."
    }

    if ($path -like "XRENGINE/Game Modes/*") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Bootstrap", "XREngine.Runtime.InputIntegration") "Bootstrap selects game-mode composition while InputIntegration owns controller and action behavior."
    }

    if ($path -like "XRENGINE/Rendering/XRWorldInstance*") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Core", "XREngine.Runtime.Rendering", "XREngine.Runtime.InputIntegration", "XREngine.Runtime.Bootstrap", "XREngine.Editor") "Decompose the cross-layer world aggregate into lifecycle, visual publication, input, composition, and editor owners."
    }

    if ($path -like "XRENGINE/Engine/Networking/*" -or $path -eq "XRENGINE/Engine/Engine.Networking.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.Core" "Networking orchestration, discovery, sessions, identity, and transport belong to the non-visual runtime core."
    }

    if ($path -eq "XRENGINE/Engine/Subclasses/Engine.Input.cs") {
        return New-OwnershipDecision Refactor "XREngine.Runtime.InputIntegration" "Replace the static input facade with explicit input/controller services owned by InputIntegration."
    }

    if ($path -like "XRENGINE/Engine/Subclasses/Rendering/*") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Rendering", "XREngine.Runtime.Bootstrap", "XREngine.Editor") "Rendering applies renderer-safe settings, Bootstrap composes them, and Editor owns preferences."
    }

    if ($path -in @(
        "XRENGINE/Engine/Engine.Windows.cs",
        "XRENGINE/Engine/Engine.ViewportRebind.cs",
        "XRENGINE/Engine/Engine.WindowPumpHost.cs")) {
        return New-OwnershipDecision Split @("XREngine.Runtime.Rendering", "XREngine.Runtime.Bootstrap") "Rendering owns window/viewport implementation while Bootstrap owns application-loop policy."
    }

    if ($path -eq "XRENGINE/Engine/NullVideoFrameGpuActions.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.Rendering" "GPU video-frame actions are renderer-facing behavior."
    }

    if ($path -in @(
        "XRENGINE/Engine/IGameLaunchBootstrap.cs",
        "XRENGINE/Engine/IGameLaunchRuntimeSmokeBootstrap.cs",
        "XRENGINE/Engine/Execution/EngineSchedulerSmokeExecutor.cs")) {
        return New-OwnershipDecision Move "XREngine.Runtime.Bootstrap" "Launch and runtime-smoke contracts belong to the shared application composition root."
    }

    if ($path -in @(
        "XRENGINE/Engine/Engine.ProfileCapture.cs",
        "XRENGINE/Engine/Engine.ProfilerSender.cs",
        "XRENGINE/Engine/Engine.MainThreadInvokeLog.cs",
        "XRENGINE/Engine/PerformanceProfileDebugHostServices.cs")) {
        return New-OwnershipDecision Split @("XREngine.Runtime.Core", "XREngine.Editor") "Runtime.Core owns diagnostic signals; Editor owns interactive capture and presentation policy."
    }

    if ($path -in @(
        "XRENGINE/Engine/Engine.Settings.cs",
        "XRENGINE/Engine/Subclasses/Engine.EffectiveSettings.cs")) {
        return New-OwnershipDecision Split @("XREngine.Runtime.Core", "XREngine.Runtime.Bootstrap", "XREngine.Editor") "Separate runtime-safe settings application, host composition, and editor preference ownership."
    }

    if ($path -eq "XRENGINE/Engine/Subclasses/Engine.State.cs") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Core", "XREngine.Runtime.InputIntegration", "XREngine.Runtime.Bootstrap") "Separate runtime/project state from local-controller creation and host composition."
    }

    if ($path -like "XRENGINE/Engine/*") {
        return New-OwnershipDecision Refactor "XREngine.Runtime.Core" "Replace the remaining static Engine partial with focused lifecycle, timing, scheduling, physics, project, and runtime services."
    }

    if ($path -like "XRENGINE/Core/Editor/*") {
        return New-OwnershipDecision Move "XREngine.Editor" "Editor state and authoring change tracking are application concerns."
    }

    if ($path -match "^XRENGINE/Core/Engine/(AnimationClip|AnimationCurve|AnimationProperty|AnimStateMachine|BlendTree|Motion)") {
        return New-OwnershipDecision Move "XREngine.Animation" "Serialization that directly requires animation domain types belongs with Animation."
    }

    if ($path -eq "XRENGINE/Core/Engine/AssetManager.RenderAssetSerializationServices.cs") {
        return New-OwnershipDecision Refactor "XREngine.Runtime.Bootstrap" "Bootstrap composes owner-provided serialization registrations without a facade static constructor."
    }

    if ($path -eq "XRENGINE/Core/Engine/AssetManagerRenderAssetSerializationServices.cs") {
        return New-OwnershipDecision Refactor "XREngine.Runtime.Core" "Runtime.Core implements the lower asset serialization service contract consumed by renderer-owned converters."
    }

    if ($path -eq "XRENGINE/Core/Engine/Loading/AssetManager.Loading.ThirdParty.cs") {
        return New-OwnershipDecision Refactor "XREngine.Runtime.Core" "Generic third-party load coordination belongs in Runtime.Core; feature-specific importer and cache policies register through lower contracts."
    }

    if ($path -eq "XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs") {
        return New-OwnershipDecision Split @("XREngine.Runtime.ModelingBridge", "XREngine.Editor") "ModelingBridge owns runtime import policy while Editor owns automatic watching and authoring UX."
    }

    if ($path -eq "XRENGINE/Core/Engine/ModelCaching/FacadeModelCachePolicyServices.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.ModelingBridge" "The temporary facade adapter disappears when ModelingBridge takes ownership of prefab import policy in P6.5."
    }

    if ($path -like "XRENGINE/Core/Engine/ModelCaching/*" -or
        $path -like "XRENGINE/Core/Engine/Loading/*ThirdParty.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.ModelingBridge" "Model caching and third-party model import belong to the explicit modeling bridge."
    }

    if ($path -in @(
        "XRENGINE/Core/Engine/AssetMetadata.cs",
        "XRENGINE/Core/Engine/InterfaceCollectionYamlNodeDeserializer.cs",
        "XRENGINE/Core/Engine/PolymorphicTypeGraphVisitor.cs",
        "XRENGINE/Core/Engine/PolymorphicYamlNodeDeserializer.cs",
        "XRENGINE/Core/Engine/TextFileYamlTypeConverter.cs",
        "XRENGINE/Core/Engine/YamlDefaultTypeInspector.cs")) {
        return New-OwnershipDecision Move "XREngine.Data" "Format-neutral metadata and YAML infrastructure belong in the lower Data layer."
    }

    if ($path -eq "XRENGINE/Core/Engine/TransformBaseYamlTypeConverter.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.Core" "Transform serialization depends on canonical Runtime.Core scene ownership."
    }

    if ($path -eq "XRENGINE/Core/Engine/SerializedAssetSupport.cs") {
        return New-OwnershipDecision Move "XREngine.Data" "Format-neutral cooked and MemoryPack model helpers belong in Data."
    }

    if ($path -eq "XRENGINE/Core/Engine/XRAssetYamlTypeConverter.cs") {
        return New-OwnershipDecision Split @("XREngine.Data", "XREngine.Runtime.Core") "Separate lower type/format resolution from runtime asset loading and publication."
    }

    if ($path -eq "XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Core", "XREngine.Animation", "XREngine.Runtime.Rendering", "XREngine.Runtime.ModelingBridge") "Runtime.Core owns generic loading/cache coordination while feature owners contribute their codecs and path policies."
    }

    if ($path -like "XRENGINE/Core/Engine/*") {
        return New-OwnershipDecision Refactor "XREngine.Runtime.Core" "Runtime asset loading, saving, watching, publication, and cache coordination belong behind focused Runtime.Core services."
    }

    if ($path -match "^XRENGINE/Core/Files/CookedBinary/Modules/Custom/(AnimationClip|AnimStateMachine|BlendTree)\.cs$") {
        return New-OwnershipDecision Move "XREngine.Animation" "Cooked modules that require animation types belong with Animation."
    }

    if ($path -eq "XRENGINE/Core/Files/PublishedCookedAssetRegistryRegistration.cs") {
        return New-OwnershipDecision Split @("XREngine.Data", "XREngine.Animation", "XREngine.Runtime.Rendering") "Data owns the registry and each feature owner contributes its published cooked serializers through leases."
    }

    if ($path -eq "XRENGINE/Core/Files/FacadeAssetSerializationRegistration.cs") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Bootstrap", "XREngine.Runtime.ModelingBridge", "XREngine.Editor") "The temporary registration adapter splits into composition, prefab type hints, and editor-settings aliases in P6.4-P6.5."
    }

    if ($path -eq "XRENGINE/Core/Files/FacadePublishedCookedAssetRegistration.cs") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Bootstrap", "XREngine.Editor") "Startup-settings serialization moves with Bootstrap while editor-preference serialization moves to Editor in P6.4."
    }

    if ($path -eq "XRENGINE/Core/Files/XRProject.cs" -or
        $path -eq "XRENGINE/Core/Files/DirectStorageIO.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.Core" "Published registry, project runtime state, and runtime IO orchestration require Runtime.Core ownership."
    }

    if ($path -like "XRENGINE/Core/Files/*") {
        return New-OwnershipDecision Move "XREngine.Data" "Asset packing, cooked formats, lower serialization modules, and file primitives are format-neutral Data responsibilities."
    }

    if ($path -eq "XRENGINE/Core/Enums/ERenderPass2D.cs" -or
        $path -like "XRENGINE/Core/Extensions/BitmapExtension.cs" -or
        $path -like "XRENGINE/Core/Extensions/GraphicsExtension.cs" -or
        $path -in @(
            "XRENGINE/Core/Interfaces/IBaseSubMesh.cs",
            "XRENGINE/Core/Interfaces/ISkeletalSubMesh.cs",
            "XRENGINE/Core/Interfaces/IStaticSubMesh.cs")) {
        return New-OwnershipDecision Move "XREngine.Runtime.Rendering" "The type is renderer-facing and has no lower runtime ownership."
    }

    if ($path -eq "XRENGINE/Core/Extensions/ControlExtension.cs" -or
        $path -eq "XRENGINE/Core/Tools/Unity/UnityConverter.cs" -or
        $path -eq "XRENGINE/Core/XRMenuItem.cs") {
        return New-OwnershipDecision Move "XREngine.Editor" "The source depends on authoring UI or editor-only conversion behavior."
    }

    if ($path -in @(
        "XRENGINE/Core/Enums/ENormalizeOption.cs",
        "XRENGINE/Core/Interfaces/IBufferable.cs",
        "XRENGINE/Core/Interfaces/Interfaces.cs",
        "XRENGINE/Core/Interfaces/ITextSource.cs",
        "XRENGINE/Core/Reflection/AssemblyQualifiedName.cs",
        "XRENGINE/Core/Serialization/XREngineJsonSerialization.cs",
        "XRENGINE/Core/SnapshotBinarySerializer.cs",
        "XRENGINE/Core/SnapshotYamlSerializer.cs")) {
        return New-OwnershipDecision Move "XREngine.Data" "The source is a lower value, serialization, or reflection contract independent of runtime ownership."
    }

    if ($path -like "XRENGINE/Core/*") {
        return New-OwnershipDecision Move "XREngine.Runtime.Core" "World state, lifecycle, play mode, time, snapshots, and platform runtime behavior belong to Runtime.Core."
    }

    if ($path -like "XRENGINE/Scene/Components/Debug/*" -or
        $path -like "XRENGINE/Scene/Components/Mesh/*" -or
        $path -eq "XRENGINE/Scene/Components/Pawns/PawnComponentRenderingExtensions.cs" -or
        $path -eq "XRENGINE/Scene/Transforms/Misc/BillboardTransform.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.Rendering" "Debug visualization, mesh publication, and camera-facing transforms belong to Runtime.Rendering."
    }

    if ($path -eq "XRENGINE/Scene/Components/Movement/PlayerMovementComponentBase.cs" -or
        $path -eq "XRENGINE/Scene/Components/Pawns/VRPlayerInputSet.cs") {
        return New-OwnershipDecision Move "XREngine.Runtime.InputIntegration" "The source converts local/VR input into runtime controller behavior."
    }

    if ($path -eq "XRENGINE/Scene/Components/Pawns/CharacterPawnComponent.cs") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Core", "XREngine.Runtime.InputIntegration") "Runtime.Core owns pawn/world state while InputIntegration owns possession and controller routing."
    }

    if ($path -like "XRENGINE/Scene/Components/UnityImport/*" -or
        $path -like "XRENGINE/Scene/Prefabs/UnityImport/*") {
        return New-OwnershipDecision Move "XREngine.Runtime.ModelingBridge" "Unity import metadata and prefab conversion products belong to the modeling/import bridge."
    }

    if ($path -eq "XRENGINE/Scene/UnityEditorImportBridge.cs") {
        return New-OwnershipDecision Move "XREngine.Editor" "The bridge invokes authoring-only Unity conversion from the editor."
    }

    if ($path -match "^XRENGINE/Scene/Components/Physics/(GPUSoftbodyComponent|PhysicsChainComponent\.(GPU|Diagnostics|QualityOutput|RenderingCompute)|PhysicsChainReadbackService|PhysicsChainWorldReadbackExtensions)" -or
        $path -match "^XRENGINE/Scene/Components/Physics/CPU/PhysicsChainCpu(RenderOutput|SkinPalette)") {
        return New-OwnershipDecision Split @("XREngine.Runtime.Core", "XREngine.Runtime.Rendering") "Keep simulation/snapshots in Runtime.Core and move GPU/readback/visual publication to Runtime.Rendering."
    }

    if ($path -like "XRENGINE/Scene/Components/Physics/Joints/*") {
        return New-OwnershipDecision Move "XREngine.Runtime.Rendering" "Joint components consume renderer-owned transform and debug-visualization services while their lower constraint contracts remain in Runtime.Core."
    }

    if ($path -like "XRENGINE/Scene/Components/Physics/*") {
        return New-OwnershipDecision Move "XREngine.Runtime.Core" "Non-visual physics components, simulation state, queries, controllers, and scheduling belong to Runtime.Core."
    }

    if ($path -like "XRENGINE/Scene/*") {
        return New-OwnershipDecision Move "XREngine.Runtime.Core" "General scene, prefab, movement, and world-owned component behavior belongs to Runtime.Core."
    }

    throw "No Phase 6 ownership rule exists for '$path'."
}

$trackedFiles = @(
    @(
        & git -C $repositoryRoot ls-files --cached --others --exclude-standard -- "XRENGINE/*.cs" "XRENGINE/**/*.cs"
        $existingRowsBySource.Keys
    ) |
        ForEach-Object { $_.Replace("\", "/") } |
        Sort-Object -Unique
)

if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed while enumerating facade sources."
}

if ($trackedFiles.Count -eq 0) {
    throw "No tracked facade C# sources were found. Keep the manifest and migrate its rows before deleting the source tree."
}

$typePattern = [regex]'(?m)^\s*(?<access>public|internal|protected|private|file)?\s*(?:(?:new|sealed|abstract|static|partial|readonly|ref|unsafe)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)'
$namespacePattern = [regex]'(?m)^\s*namespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)'

$rows = foreach ($relativePath in $trackedFiles) {
    $absolutePath = Join-Path $repositoryRoot $relativePath
    $existingRow = $existingRowsBySource[$relativePath]
    if (-not (Test-Path -LiteralPath $absolutePath)) {
        if ($null -eq $existingRow) {
            throw "Tracked facade source '$relativePath' is absent and has no existing migration row to preserve."
        }

        $decision = Get-OwnershipDecision $relativePath
        $migrationStatus = $existingRow.MigrationStatus
        $destinationPaths = $existingRow.DestinationPaths
        if ($completedMigrationDestinations.ContainsKey($relativePath)) {
            $destinationPaths = $completedMigrationDestinations[$relativePath]
            foreach ($destinationPath in $destinationPaths.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
                if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $destinationPath))) {
                    throw "Completed migration destination '$destinationPath' for '$relativePath' does not exist."
                }
            }
            $migrationStatus = "Migrated"
        }
        elseif ($migrationStatus -eq "Pending" -and $completedDeletions.Contains($relativePath)) {
            $migrationStatus = "Deleted"
            $destinationPaths = ""
        }

        [pscustomobject]@{
            SourcePath = $relativePath
            DeclaredTypes = $existingRow.DeclaredTypes
            PublicTypeDeclarations = [int]$existingRow.PublicTypeDeclarations
            Disposition = $decision.Disposition
            FinalOwners = $decision.FinalOwners -join ";"
            MigrationStatus = $migrationStatus
            DestinationPaths = $destinationPaths
            Rationale = $decision.Rationale
        }
        continue
    }

    $source = [System.IO.File]::ReadAllText($absolutePath)
    $namespaceMatch = $namespacePattern.Match($source)
    $namespaceName = if ($namespaceMatch.Success) { $namespaceMatch.Groups['name'].Value } else { "" }
    $typeMatches = @($typePattern.Matches($source))
    $declaredTypes = @(
        $typeMatches |
            ForEach-Object {
                $typeName = $_.Groups['name'].Value
                if ([string]::IsNullOrWhiteSpace($namespaceName)) { $typeName } else { "$namespaceName.$typeName" }
            } |
            Sort-Object -Unique
    )
    $publicTypeCount = @($typeMatches | Where-Object { $_.Groups['access'].Value -eq "public" }).Count
    $decision = Get-OwnershipDecision $relativePath

    [pscustomobject]@{
        SourcePath = $relativePath
        DeclaredTypes = if ($declaredTypes.Count -eq 0) { "<assembly-metadata-or-global-source>" } else { $declaredTypes -join ";" }
        PublicTypeDeclarations = $publicTypeCount
        Disposition = $decision.Disposition
        FinalOwners = $decision.FinalOwners -join ";"
        MigrationStatus = if ($null -eq $existingRow) { "Pending" } else { $existingRow.MigrationStatus }
        DestinationPaths = if ($null -eq $existingRow) { "" } else { $existingRow.DestinationPaths }
        Rationale = $decision.Rationale
    }
}

$approvedOwners = @(
    "Removed",
    "XREngine.Animation",
    "XREngine.Data",
    "XREngine.Editor",
    "XREngine.Runtime.Bootstrap",
    "XREngine.Runtime.Core",
    "XREngine.Runtime.InputIntegration",
    "XREngine.Runtime.ModelingBridge",
    "XREngine.Runtime.Rendering"
)

foreach ($row in $rows) {
    foreach ($owner in $row.FinalOwners.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        if ($owner -notin $approvedOwners) {
            throw "Unapproved owner '$owner' in '$($row.SourcePath)'."
        }
    }
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$headers = @(
    "SourcePath",
    "DeclaredTypes",
    "PublicTypeDeclarations",
    "Disposition",
    "FinalOwners",
    "MigrationStatus",
    "DestinationPaths",
    "Rationale"
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

$ownerCounts = @{}
foreach ($row in $rows) {
    foreach ($owner in $row.FinalOwners.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $ownerCounts[$owner] = 1 + ($ownerCounts[$owner] ?? 0)
    }
}

Write-Host "Wrote $($rows.Count) Phase 6 source ownership rows to '$resolvedOutputPath'."
Write-Host "Public type declarations found by the source inventory: $((($rows | Measure-Object PublicTypeDeclarations -Sum).Sum))."
$ownerCounts.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name): $($_.Value) source row(s)"
}
