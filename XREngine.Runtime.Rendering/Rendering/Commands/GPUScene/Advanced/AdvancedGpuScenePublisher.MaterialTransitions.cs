using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private AdvancedGpuSceneCommandTransition[] _plannedCommands = [];
    private AdvancedGpuSceneMaterialTransition[] _plannedMaterials = [];
    private AdvancedGpuMaterialTransitionRequest[] _plannedMaterialRequests = [];
    private AdvancedGpuMaterialRelease[] _plannedMaterialReleases = [];
    private uint[] _plannedMaterialConstantWords = [];
    private AdvancedGpuResourceBindingSource[] _plannedResourceSources = [];
    private AdvancedMaterialTextureBinding[] _plannedResolvedBindings = [];
    private AdvancedGpuResourceBindingSource[] _resourceAcquireSources = [];
    private AdvancedMaterialTextureBinding[] _resourceAcquireBindings = [];
    private AdvancedMaterialTextureBinding[] _resourceReleaseBindings = [];
    private int[] _plannedVariantSlots = [];
    private uint[] _plannedVariantSlotStamps = [];
    private int[] _plannedCommandSlots = [];
    private uint[] _plannedCommandSlotStamps = [];
    private int[] _plannedReleaseSlots = [];
    private uint[] _plannedReleaseSlotStamps = [];
    private int[] _plannedExistingMaterialSlots = [];
    private uint[] _plannedExistingMaterialSlotStamps = [];
    private IRenderCommandMesh?[] _plannedIdentitySources = [];
    private int[] _plannedIdentityPrimitiveCounts = [];
    private int[] _plannedIdentitySourceSlots = [];
    private uint[] _plannedIdentitySourceSlotStamps = [];
    private int _plannedMaterialCount;
    private int _plannedCommandCount;
    private int _plannedMaterialReleaseCount;
    private int _resourceAcquireCount;
    private int _resourceReleaseCount;
    private int _plannedIdentitySourceCount;
    private uint _plannedVariantSlotGeneration;
    private uint _plannedCommandSlotGeneration;
    private uint _plannedReleaseSlotGeneration;
    private uint _plannedExistingMaterialSlotGeneration;
    private uint _plannedIdentitySourceSlotGeneration;

    /// <summary>
    /// Resolves the compatibility outcome retained for a source primitive in the
    /// most recently planned publication.
    /// </summary>
    public bool TryGetCanonicalCompatibilityReason(
        IRenderCommandMesh source,
        int primitiveIndex,
        out EAdvancedCanonicalCompatibilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_publicationRejected || Database.PublicationFaulted)
        {
            reason = EAdvancedCanonicalCompatibilityReason.None;
            return false;
        }

        int commandIndex = FindPlannedCommand(source, primitiveIndex);
        if (commandIndex >= 0)
        {
            reason = _plannedCommands[commandIndex].CompatibilityReason;
            return reason != EAdvancedCanonicalCompatibilityReason.None;
        }

        reason = EAdvancedCanonicalCompatibilityReason.MissingCanonicalSource;
        return true;
    }

    private void EnsureMaterialTransitionCapacity(uint capacity)
    {
        int required = checked((int)capacity);
        if (_plannedCommands.Length < required)
            Array.Resize(ref _plannedCommands, required);
        int identityRequired = checked(required * 2);
        if (_plannedIdentitySources.Length < identityRequired)
        {
            Array.Resize(ref _plannedIdentitySources, identityRequired);
            Array.Resize(ref _plannedIdentityPrimitiveCounts, identityRequired);
        }
        if (_plannedMaterials.Length < required)
        {
            Array.Resize(ref _plannedMaterials, required);
            Array.Resize(ref _plannedMaterialRequests, required);
            Array.Resize(ref _plannedMaterialReleases, required);
        }

        int constantCapacity = checked(required * (int)Database.Materials.MaximumConstantWordsPerMaterial);
        int bindingCapacity = checked(required * (int)Database.Materials.MaximumTextureBindingsPerMaterial);
        if (_plannedMaterialConstantWords.Length < constantCapacity)
            Array.Resize(ref _plannedMaterialConstantWords, constantCapacity);
        if (_plannedResourceSources.Length < bindingCapacity)
        {
            Array.Resize(ref _plannedResourceSources, bindingCapacity);
            Array.Resize(ref _plannedResolvedBindings, bindingCapacity);
            Array.Resize(ref _resourceAcquireSources, bindingCapacity);
            Array.Resize(ref _resourceAcquireBindings, bindingCapacity);
        }
        int releaseBindingCapacity = checked(bindingCapacity * 2);
        if (_resourceReleaseBindings.Length < releaseBindingCapacity)
            Array.Resize(ref _resourceReleaseBindings, releaseBindingCapacity);

        int slotCapacity = checked((int)NextPowerOfTwo(checked(capacity * 2u)));
        GrowStampedSlots(ref _plannedVariantSlots, ref _plannedVariantSlotStamps, slotCapacity, ref _plannedVariantSlotGeneration);
        GrowStampedSlots(ref _plannedCommandSlots, ref _plannedCommandSlotStamps, slotCapacity, ref _plannedCommandSlotGeneration);
        GrowStampedSlots(ref _plannedReleaseSlots, ref _plannedReleaseSlotStamps, slotCapacity, ref _plannedReleaseSlotGeneration);
        GrowStampedSlots(ref _plannedExistingMaterialSlots, ref _plannedExistingMaterialSlotStamps, slotCapacity, ref _plannedExistingMaterialSlotGeneration);
        int identitySlotCapacity = checked((int)NextPowerOfTwo(checked(capacity * 4u)));
        GrowStampedSlots(ref _plannedIdentitySourceSlots, ref _plannedIdentitySourceSlotStamps, identitySlotCapacity, ref _plannedIdentitySourceSlotGeneration);
    }

    private static void GrowStampedSlots(
        ref int[] indices,
        ref uint[] stamps,
        int capacity,
        ref uint generation)
    {
        if (indices.Length >= capacity)
            return;
        Array.Resize(ref indices, capacity);
        Array.Resize(ref stamps, capacity);
        generation = 0u;
    }

    private bool TryBuildAndPreflightWholeScenePlan(GPUScene scene, out string reason)
    {
        _plannedMaterialCount = 0;
        _plannedCommandCount = checked((int)scene.TotalCommandCount);
        _plannedMaterialReleaseCount = 0;
        _resourceAcquireCount = 0;
        _resourceReleaseCount = 0;
        _plannedIdentitySourceCount = 0;
        BeginStampedPlan(ref _plannedVariantSlotGeneration, _plannedVariantSlotStamps);
        BeginStampedPlan(ref _plannedCommandSlotGeneration, _plannedCommandSlotStamps);
        BeginStampedPlan(ref _plannedReleaseSlotGeneration, _plannedReleaseSlotStamps);
        BeginStampedPlan(ref _plannedExistingMaterialSlotGeneration, _plannedExistingMaterialSlotStamps);
        BeginStampedPlan(ref _plannedIdentitySourceSlotGeneration, _plannedIdentitySourceSlotStamps);
        BeginRegistrationPreflight();

        for (int registrationIndex = 0; registrationIndex < _registrationCount; ++registrationIndex)
        {
            ref readonly AdvancedResidentRegistration registration =
                ref _registrations[registrationIndex];
            if (registration.Active && registration.Source is { } registeredSource &&
                !TryAppendPlannedIdentitySource(
                    registeredSource,
                    checked(registration.PrimitiveIndex + 1)))
            {
                reason = "The planned canonical identity-source table is full.";
                return false;
            }
        }

        for (uint commandIndex = 0u; commandIndex < scene.TotalCommandCount; ++commandIndex)
        {
            ref AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[checked((int)commandIndex)];
            plan = default;
            plan.CommandIndex = commandIndex;
            plan.RegistrationIndex = -1;
            plan.MaterialPlanIndex = -1;
            plan.CompatibilityReason =
                EAdvancedCanonicalCompatibilityReason.MissingCanonicalSource;

            if (!scene.TryGetAdvancedPreparationCommand(commandIndex, out DrawMetadata command) ||
                !scene.TryGetSourceCommand(commandIndex, out IRenderCommandMesh? source, out int primitiveIndex) ||
                source is null)
            {
                continue;
            }
            if (!TryInsertPlannedCommand(source, primitiveIndex, checked((int)commandIndex)))
            {
                reason = "The scene contains duplicate commands for one canonical (source, primitive) registration identity.";
                return false;
            }

            CaptureSourceState(
                scene,
                source,
                primitiveIndex,
                in command,
                out Matrix4x4 world,
                out Matrix4x4 previousWorld,
                out BoundsGpu bounds,
                out XRMesh? mesh,
                out XRMaterial? material,
                out int sourcePrimitiveCount);
            plan.Source = source;
            plan.PrimitiveIndex = primitiveIndex;
            plan.Command = command;
            plan.World = world;
            plan.PreviousWorld = previousWorld;
            plan.Bounds = bounds;
            plan.Geometry = CreateGeometry(scene, mesh, in bounds, in command);
            plan.RenderState = CreateRenderState(mesh, in command);
            plan.MeshVertexCount = Math.Max(0, mesh?.VertexCount ?? 0);
            plan.StructuralSignature =
                ComputeStructuralSignature(in command, mesh, material, primitiveIndex);
            plan.ContentSignature =
                ComputeContentSignature(in command, in bounds, in world, in previousWorld, material);
            if (!TryAppendPlannedIdentitySource(source, sourcePrimitiveCount))
            {
                reason = "The planned canonical identity-source table is full.";
                return false;
            }

            if (!TryResolvePlannedMaterial(
                    material,
                    in command,
                    out int materialPlanIndex,
                    out EAdvancedCanonicalCompatibilityReason compatibilityReason,
                    out bool fatal,
                    out reason))
            {
                plan.CompatibilityReason = compatibilityReason;
                if (fatal)
                    return false;
                continue;
            }

            int registrationIndex = FindRegistration(source, primitiveIndex);
            plan.Supported = true;
            plan.CompatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
            plan.RegistrationIndex = registrationIndex;
            plan.MaterialPlanIndex = materialPlanIndex;
            if (registrationIndex >= 0)
                _preflightSeenStamps[registrationIndex] = _preflightSeenGeneration;

            ref AdvancedGpuMaterialTransitionRequest request =
                ref _plannedMaterialRequests[materialPlanIndex];
            AdvancedGpuHandle existingTarget =
                _plannedMaterials[materialPlanIndex].ExistingHandle;
            if (registrationIndex < 0 ||
                _registrations[registrationIndex].Material != existingTarget)
            {
                if (request.AcquireCount == uint.MaxValue)
                {
                    reason = "The planned material draw-owner count overflowed.";
                    return false;
                }
                ++request.AcquireCount;
                if (registrationIndex >= 0 &&
                    !TryAppendMaterialRelease(
                        _registrations[registrationIndex].Material,
                        1u,
                        out reason))
                {
                    return false;
                }
            }
        }

        for (int registrationIndex = 0; registrationIndex < _registrationCount; ++registrationIndex)
        {
            ref readonly AdvancedResidentRegistration registration =
                ref _registrations[registrationIndex];
            if (registration.Active &&
                _preflightSeenStamps[registrationIndex] != _preflightSeenGeneration &&
                !TryAppendMaterialRelease(registration.Material, 1u, out reason))
            {
                return false;
            }
        }

        Span<AdvancedGpuMaterialTransitionRequest> requests =
            _plannedMaterialRequests.AsSpan(0, _plannedMaterialCount);
        ReadOnlySpan<AdvancedGpuMaterialRelease> releases =
            _plannedMaterialReleases.AsSpan(0, _plannedMaterialReleaseCount);
        if (!_materialPublisher.TryPreflightTransition(requests, releases, out reason))
            return false;
        if (!TryAppendFinalMaterialResourceReleases(releases, out reason))
            return false;
        if (!_resourcePublisher.TryPreflightTransition(
                _resourceAcquireSources.AsSpan(0, _resourceAcquireCount),
                _resourceReleaseBindings.AsSpan(0, _resourceReleaseCount),
                out reason))
        {
            return false;
        }
        if (!CanApplyPlannedSceneMutations())
        {
            reason = "The canonical scene tables cannot accept the complete planned publication.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryResolvePlannedMaterial(
        XRMaterial? material,
        in DrawMetadata command,
        out int materialPlanIndex,
        out EAdvancedCanonicalCompatibilityReason compatibilityReason,
        out bool fatal,
        out string reason)
    {
        materialPlanIndex = -1;
        compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
        fatal = false;
        reason = string.Empty;
        if (command.RenderPass > int.MaxValue ||
            !MaterialBindingLayouts.TryGetDefaultForRenderPass(
                checked((int)command.RenderPass),
                out MaterialBindingLayout layout) ||
            !AdvancedGpuMaterialPublisher.TryTranslateLayout(
                layout,
                out AdvancedMaterialLayoutTranslation translation,
                out _))
        {
            compatibilityReason =
                EAdvancedCanonicalCompatibilityReason.UnsupportedRenderPass;
            return false;
        }

        EGpuMaterialStateClass expectedLegacyState = ReferenceEquals(
            layout,
            MaterialBindingLayouts.OpaqueDeferred)
                ? EGpuMaterialStateClass.OpaqueDeferred
                : ReferenceEquals(layout, MaterialBindingLayouts.ForwardOpaque)
                    ? EGpuMaterialStateClass.OpaqueForward
                    : EGpuMaterialStateClass.AlphaTested;
        if (command.StateClassID != (uint)expectedLegacyState)
        {
            compatibilityReason =
                EAdvancedCanonicalCompatibilityReason.LegacyStateMismatch;
            return false;
        }

        bool doubleSided =
            (command.Flags & (uint)GPUIndirectRenderFlags.DoubleSided) != 0u;
        EAdvancedMaterialRenderStateClass state = translation.RequiredCoverage switch
        {
            EAdvancedMaterialCoverageMode.Opaque when doubleSided =>
                EAdvancedMaterialRenderStateClass.OpaqueDoubleSided,
            EAdvancedMaterialCoverageMode.Opaque =>
                EAdvancedMaterialRenderStateClass.OpaqueSingleSided,
            EAdvancedMaterialCoverageMode.Masked when doubleSided =>
                EAdvancedMaterialRenderStateClass.MaskedDoubleSided,
            EAdvancedMaterialCoverageMode.Masked =>
                EAdvancedMaterialRenderStateClass.MaskedSingleSided,
            _ => EAdvancedMaterialRenderStateClass.Invalid,
        };
        if (state == EAdvancedMaterialRenderStateClass.Invalid)
        {
            compatibilityReason =
                EAdvancedCanonicalCompatibilityReason.UnsupportedRenderPass;
            return false;
        }

        materialPlanIndex = FindPlannedMaterial(
            material,
            layout,
            translation.RequiredCoverage,
            state);
        if (materialPlanIndex >= 0)
            return true;

        materialPlanIndex = _plannedMaterialCount;
        int constantOffset = GetMaterialConstantOffset(materialPlanIndex);
        int bindingOffset = GetMaterialBindingOffset(materialPlanIndex);
        Span<uint> constantWords = _plannedMaterialConstantWords.AsSpan(
            constantOffset,
            checked((int)layout.RowWordCount));
        MaterialBindingSourceSnapshot sourceSnapshot =
            MaterialBindingSourceEncoder.Encode(material);
        if (material is null)
        {
            MaterialBindingRowPacker.WriteDefaultRow(layout, constantWords);
        }
        else if (!MaterialBindingRowPacker.TryWriteOpaqueDeferred(
                     layout,
                     sourceSnapshot.Entry,
                     constantWords,
                     out reason))
        {
            fatal = true;
            return false;
        }

        Span<AdvancedGpuResourceBindingSource> resourceSources =
            _plannedResourceSources.AsSpan(bindingOffset, layout.Textures.Count);
        if (!TryEncodeMaterialResourceSources(
                in sourceSnapshot,
                resourceSources,
                out compatibilityReason,
                out reason))
        {
            return false;
        }

        bool existing = _materialPublisher.TryFindVariant(
            material,
            layout,
            translation.RequiredCoverage,
            state,
            out AdvancedGpuHandle existingHandle);
        bool resourcesChanged = true;
        bool constantsChanged = true;
        bool previousBindingsQueued = false;
        if (existing)
        {
            if (!Database.Materials.Materials.TryGet(
                    existingHandle,
                    out AdvancedMaterialRecord current) ||
                !Database.Materials.TryGetConstantWords(
                    current,
                    out ReadOnlySpan<uint> currentConstants) ||
                !Database.Materials.TryGetTextureBindings(
                    current,
                    out ReadOnlySpan<AdvancedMaterialTextureBinding> currentBindings))
            {
                fatal = true;
                reason = "A registered material variant lost its canonical payload.";
                return false;
            }

            constantsChanged = !currentConstants.SequenceEqual(constantWords);
            resourcesChanged = currentBindings.Length != resourceSources.Length;
            if (!resourcesChanged)
            {
                for (int bindingIndex = 0; bindingIndex < resourceSources.Length; ++bindingIndex)
                {
                    if (_resourcePublisher.BindingMatches(
                            in currentBindings[bindingIndex],
                            in resourceSources[bindingIndex]))
                    {
                        continue;
                    }
                    resourcesChanged = true;
                    break;
                }
            }

            if (resourcesChanged)
            {
                if (!TryAppendResourceReleases(currentBindings, out reason))
                {
                    fatal = true;
                    return false;
                }
                previousBindingsQueued = true;
            }
            else
            {
                currentBindings.CopyTo(
                    _plannedResolvedBindings.AsSpan(
                        bindingOffset,
                        currentBindings.Length));
            }
        }

        int acquireOffset = -1;
        if (!existing || resourcesChanged)
        {
            acquireOffset = _resourceAcquireCount;
            resourceSources.CopyTo(
                _resourceAcquireSources.AsSpan(
                    _resourceAcquireCount,
                    resourceSources.Length));
            _resourceAcquireCount += resourceSources.Length;
        }

        bool headerChanged = existing && !resourcesChanged &&
            !_materialPublisher.HeaderMatches(
                existingHandle,
                translation.RequiredCoverage,
                state,
                _plannedResolvedBindings.AsSpan(
                    bindingOffset,
                    resourceSources.Length));
        _plannedMaterials[materialPlanIndex] = new AdvancedGpuSceneMaterialTransition
        {
            ExistingHandle = existingHandle,
            ConstantOffset = constantOffset,
            BindingOffset = bindingOffset,
            ResourceAcquireOffset = acquireOffset,
            ResourceCount = resourceSources.Length,
            ResourcesChanged = !existing || resourcesChanged,
            PreviousBindingsQueued = previousBindingsQueued,
        };
        _plannedMaterialRequests[materialPlanIndex] =
            new AdvancedGpuMaterialTransitionRequest(
                material,
                layout,
                translation.RequiredCoverage,
                state,
                layout.RowWordCount,
                checked((uint)resourceSources.Length),
                0u,
                existing && (constantsChanged || resourcesChanged || headerChanged));
        ++_plannedMaterialCount;
        InsertPlannedMaterial(materialPlanIndex);
        if (existing)
            InsertPlannedExistingMaterial(materialPlanIndex);
        return true;
    }

    private static bool TryEncodeMaterialResourceSources(
        in MaterialBindingSourceSnapshot snapshot,
        Span<AdvancedGpuResourceBindingSource> destination,
        out EAdvancedCanonicalCompatibilityReason compatibilityReason,
        out string reason)
    {
        if (destination.Length != 3)
        {
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
            reason = "The bounded material bridge requires exactly three texture slots.";
            return false;
        }
        if (!AdvancedGpuResourceSourceEncoder.TryEncode(
                snapshot.Albedo,
                EAdvancedResourceFallback.White,
                out destination[0],
                out compatibilityReason,
                out reason) ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(
                snapshot.Normal,
                EAdvancedResourceFallback.FlatNormal,
                out destination[1],
                out compatibilityReason,
                out reason) ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(
                snapshot.RM,
                EAdvancedResourceFallback.White,
                out destination[2],
                out compatibilityReason,
                out reason))
        {
            return false;
        }

        compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
        reason = string.Empty;
        return true;
    }

    private bool TryAppendPlannedIdentitySource(
        IRenderCommandMesh source,
        int primitiveCount)
    {
        primitiveCount = Math.Max(1, primitiveCount);
        if (_identityHandleScratch.Length < primitiveCount)
        {
            Array.Resize(
                ref _identityHandleScratch,
                checked((int)NextPowerOfTwo(checked((uint)primitiveCount))));
        }
        uint mask = checked((uint)_plannedIdentitySourceSlots.Length - 1u);
        uint start = IdentitySourceHash(source) & mask;
        for (uint probe = 0u;
             probe < (uint)_plannedIdentitySourceSlots.Length;
             ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedIdentitySourceSlotStamps[slot] !=
                _plannedIdentitySourceSlotGeneration)
            {
                if (_plannedIdentitySourceCount >= _plannedIdentitySources.Length)
                    return false;
                int index = _plannedIdentitySourceCount++;
                _plannedIdentitySources[index] = source;
                _plannedIdentityPrimitiveCounts[index] = primitiveCount;
                _plannedIdentitySourceSlots[slot] = index;
                _plannedIdentitySourceSlotStamps[slot] =
                    _plannedIdentitySourceSlotGeneration;
                return true;
            }

            int existingIndex = _plannedIdentitySourceSlots[slot];
            if (!ReferenceEquals(_plannedIdentitySources[existingIndex], source))
                continue;
            _plannedIdentityPrimitiveCounts[existingIndex] = Math.Max(
                _plannedIdentityPrimitiveCounts[existingIndex],
                primitiveCount);
            return true;
        }

        return false;
    }

    private static uint IdentitySourceHash(IRenderCommandMesh source)
    {
        uint hash = unchecked((uint)RuntimeHelpers.GetHashCode(source));
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        return hash ^ (hash >> 15);
    }

    private bool TryAppendMaterialRelease(
        AdvancedGpuHandle material,
        uint count,
        out string reason)
    {
        int slot = FindPlannedReleaseSlot(material);
        if (slot >= 0)
        {
            int releaseIndex = _plannedReleaseSlots[slot];
            AdvancedGpuMaterialRelease current =
                _plannedMaterialReleases[releaseIndex];
            if (current.Count > uint.MaxValue - count)
            {
                reason = "The planned material release count overflowed.";
                return false;
            }
            _plannedMaterialReleases[releaseIndex] =
                new AdvancedGpuMaterialRelease(material, current.Count + count);
            reason = string.Empty;
            return true;
        }

        int index = _plannedMaterialReleaseCount++;
        _plannedMaterialReleases[index] =
            new AdvancedGpuMaterialRelease(material, count);
        InsertPlannedRelease(material, index);
        reason = string.Empty;
        return true;
    }

    private bool TryAppendFinalMaterialResourceReleases(
        ReadOnlySpan<AdvancedGpuMaterialRelease> releases,
        out string reason)
    {
        for (int releaseIndex = 0; releaseIndex < releases.Length; ++releaseIndex)
        {
            ref readonly AdvancedGpuMaterialRelease release = ref releases[releaseIndex];
            if (!_materialPublisher.TryGetReferenceCount(
                    release.Material,
                    out uint currentReferences))
            {
                reason = "A planned material release references an unknown variant.";
                return false;
            }

            int materialPlanIndex = FindPlannedExistingMaterial(release.Material);
            uint acquired = materialPlanIndex < 0
                ? 0u
                : _plannedMaterialRequests[materialPlanIndex].AcquireCount;
            ulong available = (ulong)currentReferences + acquired;
            if (release.Count != available)
                continue;
            if (materialPlanIndex >= 0 &&
                _plannedMaterials[materialPlanIndex].PreviousBindingsQueued)
            {
                continue;
            }

            if (!Database.Materials.Materials.TryGet(
                    release.Material,
                    out AdvancedMaterialRecord current) ||
                !Database.Materials.TryGetTextureBindings(
                    current,
                    out ReadOnlySpan<AdvancedMaterialTextureBinding> bindings))
            {
                reason = "A retiring material variant lost its canonical resource bindings.";
                return false;
            }
            if (!TryAppendResourceReleases(bindings, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryAppendResourceReleases(
        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings,
        out string reason)
    {
        if (bindings.Length > _resourceReleaseBindings.Length - _resourceReleaseCount)
        {
            reason = "The preallocated material resource-release plan is full.";
            return false;
        }
        bindings.CopyTo(
            _resourceReleaseBindings.AsSpan(
                _resourceReleaseCount,
                bindings.Length));
        _resourceReleaseCount += bindings.Length;
        reason = string.Empty;
        return true;
    }

    private bool CanApplyPlannedSceneMutations()
    {
        int additions = 0;
        int structuralUpdates = 0;
        int contentUpdates = 0;
        for (int commandIndex = 0; commandIndex < _plannedCommandCount; ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            if (plan.CommandIndex >= (uint)_commandDrawHandles.Length || !plan.Supported)
                continue;
            if (plan.RegistrationIndex < 0)
            {
                ++additions;
                continue;
            }

            ref readonly AdvancedResidentRegistration registration =
                ref _registrations[plan.RegistrationIndex];
            if (!IsRegistrationCurrent(in registration))
                return false;
            AdvancedGpuHandle existingTarget =
                _plannedMaterials[plan.MaterialPlanIndex].ExistingHandle;
            if (plan.StructuralSignature != registration.StructuralSignature ||
                !existingTarget.IsValid ||
                registration.Material != existingTarget)
            {
                ++structuralUpdates;
            }
            if (plan.ContentSignature != registration.ContentSignature)
                ++contentUpdates;
        }

        int tombstones = 0;
        for (int index = 0; index < _registrationCount; ++index)
            if (_registrations[index].Active &&
                _preflightSeenStamps[index] != _preflightSeenGeneration)
            {
                if (!IsRegistrationCurrent(in _registrations[index]))
                    return false;
                ++tombstones;
            }

        AdvancedGpuSceneDatabase tables = Database.Scene;
        return tables.Draws.CanApply(additions, structuralUpdates, tombstones) &&
            tables.Instances.CanApply(additions, contentUpdates, tombstones) &&
            tables.Transforms.CanApply(
                checked(additions * 2),
                checked(contentUpdates * 2),
                checked(tombstones * 2)) &&
            tables.Geometry.Records.CanApply(additions, structuralUpdates, tombstones) &&
            tables.Deformations.CanApply(additions, structuralUpdates, tombstones) &&
            tables.RenderStates.CanApply(additions, structuralUpdates, tombstones) &&
            tables.EditorIdentities.CanApply(additions, contentUpdates, tombstones);
    }

    private void ApplyPreflightedMaterialTransitions()
    {
        _resourcePublisher.ApplyPreflightedAcquisitions(
            _resourceAcquireSources.AsSpan(0, _resourceAcquireCount),
            _resourceAcquireBindings.AsSpan(0, _resourceAcquireCount));
        for (int materialIndex = 0; materialIndex < _plannedMaterialCount; ++materialIndex)
        {
            ref readonly AdvancedGpuSceneMaterialTransition plan =
                ref _plannedMaterials[materialIndex];
            if (!plan.ResourcesChanged)
                continue;
            _resourceAcquireBindings.AsSpan(
                    plan.ResourceAcquireOffset,
                    plan.ResourceCount)
                .CopyTo(_plannedResolvedBindings.AsSpan(
                    plan.BindingOffset,
                    plan.ResourceCount));
        }

        for (int materialIndex = 0; materialIndex < _plannedMaterialCount; ++materialIndex)
        {
            ref AdvancedGpuMaterialTransitionRequest request =
                ref _plannedMaterialRequests[materialIndex];
            ref readonly AdvancedGpuSceneMaterialTransition plan =
                ref _plannedMaterials[materialIndex];
            bool updatesExisting = request.MaterialHandle.IsValid &&
                request.RequiresPayloadUpdate;
            _materialPublisher.ApplyPreflightedRequest(
                ref request,
                _plannedMaterialConstantWords.AsSpan(
                    plan.ConstantOffset,
                    checked((int)request.ConstantWordCount)),
                _plannedResolvedBindings.AsSpan(
                    plan.BindingOffset,
                    checked((int)request.TextureBindingCount)));
            if (updatesExisting)
            {
                ++_contentDeltaCount;
                AdvanceNonZero(ref _contentGeneration);
            }
        }
    }

    private void CompletePreflightedMaterialTransitions()
    {
        _materialPublisher.ApplyPreflightedReleases(
            _plannedMaterialReleases.AsSpan(
                0,
                _plannedMaterialReleaseCount));
        _resourcePublisher.ApplyPreflightedReleases();
    }

    private int GetMaterialConstantOffset(int materialPlanIndex)
        => checked(materialPlanIndex *
            (int)Database.Materials.MaximumConstantWordsPerMaterial);

    private int GetMaterialBindingOffset(int materialPlanIndex)
        => checked(materialPlanIndex *
            (int)Database.Materials.MaximumTextureBindingsPerMaterial);

    private void BeginRegistrationPreflight()
    {
        ++_preflightSeenGeneration;
        if (_preflightSeenGeneration != 0u)
            return;
        Array.Clear(_preflightSeenStamps);
        _preflightSeenGeneration = 1u;
    }

    private static void BeginStampedPlan(ref uint generation, uint[] stamps)
    {
        ++generation;
        if (generation != 0u)
            return;
        Array.Clear(stamps);
        generation = 1u;
    }

    private int FindPlannedMaterial(
        XRMaterial? material,
        MaterialBindingLayout layout,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state)
    {
        uint mask = checked((uint)_plannedVariantSlots.Length - 1u);
        uint start = HashPlannedMaterial(material, layout, coverage, state) & mask;
        for (uint probe = 0u; probe < (uint)_plannedVariantSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedVariantSlotStamps[slot] != _plannedVariantSlotGeneration)
                return -1;
            int index = _plannedVariantSlots[slot];
            ref readonly AdvancedGpuMaterialTransitionRequest candidate =
                ref _plannedMaterialRequests[index];
            if (ReferenceEquals(candidate.Material, material) &&
                ReferenceEquals(candidate.Layout, layout) &&
                candidate.Coverage == coverage &&
                candidate.State == state)
            {
                return index;
            }
        }
        return -1;
    }

    private void InsertPlannedMaterial(int materialPlanIndex)
    {
        ref readonly AdvancedGpuMaterialTransitionRequest request =
            ref _plannedMaterialRequests[materialPlanIndex];
        uint mask = checked((uint)_plannedVariantSlots.Length - 1u);
        uint start = HashPlannedMaterial(
            request.Material,
            request.Layout,
            request.Coverage,
            request.State) & mask;
        for (uint probe = 0u; probe < (uint)_plannedVariantSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedVariantSlotStamps[slot] == _plannedVariantSlotGeneration)
                continue;
            _plannedVariantSlots[slot] = materialPlanIndex;
            _plannedVariantSlotStamps[slot] = _plannedVariantSlotGeneration;
            return;
        }
        throw new InvalidOperationException("The preallocated material-plan lookup is full.");
    }

    private bool TryInsertPlannedCommand(
        IRenderCommandMesh source,
        int primitiveIndex,
        int commandIndex)
    {
        uint mask = checked((uint)_plannedCommandSlots.Length - 1u);
        uint start = HashRegistration(source, primitiveIndex) & mask;
        for (uint probe = 0u; probe < (uint)_plannedCommandSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedCommandSlotStamps[slot] != _plannedCommandSlotGeneration)
            {
                _plannedCommandSlots[slot] = commandIndex;
                _plannedCommandSlotStamps[slot] = _plannedCommandSlotGeneration;
                return true;
            }
            ref readonly AdvancedGpuSceneCommandTransition candidate =
                ref _plannedCommands[_plannedCommandSlots[slot]];
            if (ReferenceEquals(candidate.Source, source) &&
                candidate.PrimitiveIndex == primitiveIndex)
            {
                return false;
            }
        }
        return false;
    }

    private int FindPlannedCommand(IRenderCommandMesh source, int primitiveIndex)
    {
        if (_plannedCommandSlots.Length == 0)
            return -1;
        uint mask = checked((uint)_plannedCommandSlots.Length - 1u);
        uint start = HashRegistration(source, primitiveIndex) & mask;
        for (uint probe = 0u; probe < (uint)_plannedCommandSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedCommandSlotStamps[slot] != _plannedCommandSlotGeneration)
                return -1;
            int index = _plannedCommandSlots[slot];
            ref readonly AdvancedGpuSceneCommandTransition candidate =
                ref _plannedCommands[index];
            if (ReferenceEquals(candidate.Source, source) &&
                candidate.PrimitiveIndex == primitiveIndex)
            {
                return index;
            }
        }
        return -1;
    }

    private int FindPlannedReleaseSlot(AdvancedGpuHandle material)
    {
        uint mask = checked((uint)_plannedReleaseSlots.Length - 1u);
        uint start = HashMaterialHandle(material) & mask;
        for (uint probe = 0u; probe < (uint)_plannedReleaseSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedReleaseSlotStamps[slot] != _plannedReleaseSlotGeneration)
                return -1;
            if (_plannedMaterialReleases[_plannedReleaseSlots[slot]].Material == material)
                return slot;
        }
        return -1;
    }

    private void InsertPlannedRelease(AdvancedGpuHandle material, int releaseIndex)
    {
        uint mask = checked((uint)_plannedReleaseSlots.Length - 1u);
        uint start = HashMaterialHandle(material) & mask;
        for (uint probe = 0u; probe < (uint)_plannedReleaseSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedReleaseSlotStamps[slot] == _plannedReleaseSlotGeneration)
                continue;
            _plannedReleaseSlots[slot] = releaseIndex;
            _plannedReleaseSlotStamps[slot] = _plannedReleaseSlotGeneration;
            return;
        }
        throw new InvalidOperationException("The preallocated material-release lookup is full.");
    }

    private void InsertPlannedExistingMaterial(int materialPlanIndex)
    {
        AdvancedGpuHandle material =
            _plannedMaterials[materialPlanIndex].ExistingHandle;
        uint mask = checked((uint)_plannedExistingMaterialSlots.Length - 1u);
        uint start = HashMaterialHandle(material) & mask;
        for (uint probe = 0u; probe < (uint)_plannedExistingMaterialSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedExistingMaterialSlotStamps[slot] ==
                _plannedExistingMaterialSlotGeneration)
            {
                continue;
            }
            _plannedExistingMaterialSlots[slot] = materialPlanIndex;
            _plannedExistingMaterialSlotStamps[slot] =
                _plannedExistingMaterialSlotGeneration;
            return;
        }
        throw new InvalidOperationException("The preallocated existing-material lookup is full.");
    }

    private int FindPlannedExistingMaterial(AdvancedGpuHandle material)
    {
        uint mask = checked((uint)_plannedExistingMaterialSlots.Length - 1u);
        uint start = HashMaterialHandle(material) & mask;
        for (uint probe = 0u; probe < (uint)_plannedExistingMaterialSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedExistingMaterialSlotStamps[slot] !=
                _plannedExistingMaterialSlotGeneration)
            {
                return -1;
            }
            int index = _plannedExistingMaterialSlots[slot];
            if (_plannedMaterials[index].ExistingHandle == material)
                return index;
        }
        return -1;
    }

    private static uint HashPlannedMaterial(
        XRMaterial? material,
        MaterialBindingLayout layout,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state)
    {
        uint hash = unchecked((uint)RuntimeHelpers.GetHashCode(material!));
        hash = MixPlanHash(hash, unchecked((uint)RuntimeHelpers.GetHashCode(layout)));
        hash = MixPlanHash(hash, (uint)coverage);
        return MixPlanHash(hash, (uint)state);
    }

    private static uint HashMaterialHandle(AdvancedGpuHandle material)
        => MixPlanHash(material.Index, material.Generation);

    private static uint MixPlanHash(uint hash, uint value)
    {
        hash ^= value + 0x9E3779B9u + (hash << 6) + (hash >> 2);
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        return hash ^ (hash >> 15);
    }

    private bool IsRegistrationCurrent(
        in AdvancedResidentRegistration registration)
        => Database.Scene.Draws.IsCurrent(registration.Draw) &&
            Database.Scene.Instances.IsCurrent(registration.Instance) &&
            Database.Scene.Geometry.Records.IsCurrent(registration.Geometry) &&
            Database.Scene.Deformations.IsCurrent(registration.Deformation) &&
            Database.Materials.Materials.IsCurrent(registration.Material) &&
            Database.Scene.RenderStates.IsCurrent(registration.RenderState) &&
            Database.Scene.EditorIdentities.IsCurrent(registration.EditorIdentity) &&
            Database.Scene.Transforms.IsCurrent(registration.CurrentTransform) &&
            Database.Scene.Transforms.IsCurrent(registration.PreviousTransform);

    private struct AdvancedGpuSceneCommandTransition
    {
        public IRenderCommandMesh? Source;
        public DrawMetadata Command;
        public Matrix4x4 World;
        public Matrix4x4 PreviousWorld;
        public BoundsGpu Bounds;
        public AdvancedGeometryRecord Geometry;
        public AdvancedRenderStateRecord RenderState;
        public ulong StructuralSignature;
        public ulong ContentSignature;
        public uint CommandIndex;
        public int PrimitiveIndex;
        public int RegistrationIndex;
        public int MaterialPlanIndex;
        public int MeshVertexCount;
        public EAdvancedCanonicalCompatibilityReason CompatibilityReason;
        public bool Supported;
    }

    private struct AdvancedGpuSceneMaterialTransition
    {
        public AdvancedGpuHandle ExistingHandle;
        public int ConstantOffset;
        public int BindingOffset;
        public int ResourceAcquireOffset;
        public int ResourceCount;
        public bool ResourcesChanged;
        public bool PreviousBindingsQueued;
    }
}
