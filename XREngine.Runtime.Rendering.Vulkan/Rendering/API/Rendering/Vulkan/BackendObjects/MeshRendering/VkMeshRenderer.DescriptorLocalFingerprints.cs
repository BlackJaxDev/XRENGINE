using System;
using System.Collections.Generic;

using Silk.NET.Vulkan;

using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private const int LocalDescriptorFingerprintMemoCapacity = 8;
    private readonly (DescriptorLocalFingerprintMemoKey Key, ulong Fingerprint, bool Valid)[]
        _localDescriptorFingerprintMemo = new (DescriptorLocalFingerprintMemoKey, ulong, bool)[LocalDescriptorFingerprintMemoCapacity];
    private int _nextLocalDescriptorFingerprintMemoSlot;

    /// <summary>Chooses allocation identity from the physical local writes when eligible.</summary>
    private bool TryResolveMeshDescriptorAllocationResourceFingerprint(
        XRMaterial material,
        IReadOnlyList<DescriptorBindingInfo> bindings,
        int frameCount,
        int setCount,
        uint activeSetMask,
        int drawUniformSlot,
        bool usesSharedMaterialTier,
        bool descriptorBindingsAreDrawSlotInvariant,
        bool hasFrameSourceDescriptors,
        int viewFamilyIdentity,
        ComputeDispatchSnapshot? bindingSnapshot,
        ulong resourceFingerprint,
        ulong stableResourceFingerprint,
        out ulong allocationResourceFingerprint)
    {
        if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
        {
            allocationResourceFingerprint = _heapDescriptorAllocationOwnerIdentity;
            return true;
        }

        if (TryGetLocalPhysicalDescriptorFingerprint(
                material,
                bindings,
                frameCount,
                setCount,
                activeSetMask,
                drawUniformSlot,
                usesSharedMaterialTier,
                viewFamilyIdentity,
                bindingSnapshot,
                out ulong localPhysicalFingerprint))
        {
            allocationResourceFingerprint = localPhysicalFingerprint;
            return true;
        }

        // A local buffer layout must never switch to whole-snapshot allocation
        // identity merely because its physical binding raced or was unresolved.
        if (IsLocalDynamicUniformLayout(
                bindings,
                activeSetMask,
                bindingSnapshot))
        {
            allocationResourceFingerprint = 0UL;
            return false;
        }

        allocationResourceFingerprint = ResolveDescriptorAllocationImmutableResourceFingerprint(
            descriptorBindingsAreDrawSlotInvariant,
            DescriptorSetsAreUpdateAfterBind(activeSetMask),
            bindingSnapshot is not null,
            hasFrameSourceDescriptors,
            resourceFingerprint,
            stableResourceFingerprint);
        return true;
    }

    /// <summary>Determines whether local buffer contents require physical identity.</summary>
    private bool IsLocalDynamicUniformLayout(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        uint activeSetMask,
        ComputeDispatchSnapshot? bindingSnapshot)
        => _program is { } program &&
           activeSetMask != 0 &&
           BackendContext.Resources.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap &&
           bindingSnapshot is { HasPublishedBindingLayoutSignatures: true, HasReadOnlyStorageBindings: false } &&
           BackendContext.Resources.MappedFrameArena is { IsActive: true } &&
           LocalDynamicUniformSourcesRemainEligible(
               program,
               bindings,
               activeSetMask,
               bindingSnapshot);

    /// <summary>
    /// Identifies immutable local descriptor contents from the buffers actually
    /// written to conventional sets. Other reflected layouts retain the full
    /// snapshot identity because their physical dependencies are less restricted.
    /// </summary>
    private bool TryGetLocalPhysicalDescriptorFingerprint(
        XRMaterial material,
        IReadOnlyList<DescriptorBindingInfo> bindings,
        int frameCount,
        int setCount,
        uint activeSetMask,
        int drawUniformSlot,
        bool usesSharedMaterialTier,
        int viewFamilyIdentity,
        ComputeDispatchSnapshot? bindingSnapshot,
        out ulong fingerprint,
        int revisionRetry = 0)
    {
        fingerprint = 0UL;
        if (_program is not { } program ||
            frameCount <= 0 ||
            activeSetMask == 0 ||
            BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap ||
            bindingSnapshot is not { HasPublishedBindingLayoutSignatures: true, HasReadOnlyStorageBindings: false } ||
            BackendContext.Resources.MappedFrameArena is not { IsActive: true } frameArena)
            return false;

        bindingSnapshot.ResolvePublishedResourceSignatures(
            viewFamilyIdentity,
            out _,
            out ulong snapshotResourceSignature);
        ulong bufferRevision = BackendContext.Resources.NativeBufferBindingRevision;
        DescriptorLocalFingerprintMemoKey key = new(
            program,
            material,
            bindings,
            program.BindingId,
            program.LinkGeneration,
            program.DescriptorLayoutFingerprint,
            program.DescriptorSchemaFingerprint,
            material.BindingLayoutVersion,
            material.BindingResourceVersion,
            frameCount,
            setCount,
            activeSetMask,
            usesSharedMaterialTier,
            drawUniformSlot,
            viewFamilyIdentity,
            bindingSnapshot.DescriptorSetLayoutSignature,
            snapshotResourceSignature,
            bindingSnapshot.StablePersistentEngineResourceSignature,
            bindingSnapshot.PreparedMaterialTableSignature,
            ComputeCachedBufferResourceFingerprintCore(),
            frameArena.Identity,
            frameArena.Generation,
            bufferRevision);

        for (int i = 0; i < _localDescriptorFingerprintMemo.Length; i++)
        {
            ref var entry = ref _localDescriptorFingerprintMemo[i];
            if (!entry.Valid || !entry.Key.Matches(in key))
                continue;
            // Ambient buffer routing can change without replacing the captured
            // snapshot. A memo hit still has to prove the resolver source.
            if (!LocalDynamicUniformSourcesRemainEligible(
                    program,
                    bindings,
                    activeSetMask,
                    bindingSnapshot) ||
                BackendContext.Resources.NativeBufferBindingRevision != bufferRevision)
                return false;
            fingerprint = entry.Fingerprint;
            return true;
        }

        FrameOpSignatureHasher hash = new();
        hash.Add(frameCount);
        int localBindingCount = 0;
        for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
        {
            DescriptorBindingInfo binding = bindings[bindingIndex];
            if (binding.Set >= 32 || (activeSetMask & (1u << (int)binding.Set)) == 0)
                continue;
            if (binding.DescriptorType != DescriptorType.UniformBufferDynamic ||
                VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding) != 1 ||
                bindingSnapshot.Buffers.ContainsKey(binding.Binding) ||
                !IsKnownLocalDynamicUniformBinding(program, binding, out bool isAutoUniform))
                return false;

            localBindingCount++;
            hash.Add(binding.Set);
            hash.Add(binding.Binding);
            hash.Add((int)binding.DescriptorType);
            hash.Add(1u);
            hash.Add((int)binding.Requirement);
            for (int frameSlot = 0; frameSlot < frameCount; frameSlot++)
            {
                bool expectedSourceResolved = isAutoUniform
                    ? TryResolveAutoUniformBuffer(
                        binding,
                        frameSlot,
                        drawUniformSlot,
                        out DescriptorBufferInfo expectedInfo)
                    : TryResolveEngineUniformBuffer(
                        binding,
                        frameSlot,
                        drawUniformSlot,
                        out expectedInfo);
                if (!expectedSourceResolved ||
                    !TryResolveBuffer(
                        binding,
                        frameSlot,
                        drawUniformSlot,
                        out DescriptorBufferInfo info,
                        bindingSnapshot) ||
                    expectedInfo.Buffer.Handle != info.Buffer.Handle ||
                    expectedInfo.Offset != info.Offset ||
                    expectedInfo.Range != info.Range ||
                    info.Buffer.Handle == 0 ||
                    info.Range == 0)
                    return false;

                ulong generation = GetResourceGeneration(ObjectType.Buffer, info.Buffer.Handle);
                if (generation == 0)
                    return false;
                hash.Add(true);
                hash.Add(info.Buffer.Handle);
                hash.Add(generation);
                hash.Add(info.Offset);
                hash.Add(info.Range);
            }
        }

        if (localBindingCount == 0)
            return false;

        if (BackendContext.Resources.NativeBufferBindingRevision != bufferRevision ||
            ComputeCachedBufferResourceFingerprintCore() != key.RendererBufferSignature)
        {
            return revisionRetry == 0 &&
                   TryGetLocalPhysicalDescriptorFingerprint(
                       material,
                       bindings,
                       frameCount,
                       setCount,
                       activeSetMask,
                       drawUniformSlot,
                       usesSharedMaterialTier,
                       viewFamilyIdentity,
                       bindingSnapshot,
                       out fingerprint,
                       revisionRetry: 1);
        }

        fingerprint = hash.ToHash();
        ref var next = ref _localDescriptorFingerprintMemo[_nextLocalDescriptorFingerprintMemoSlot];
        next = (key, fingerprint, true);
        _nextLocalDescriptorFingerprintMemoSlot =
            (_nextLocalDescriptorFingerprintMemoSlot + 1) % LocalDescriptorFingerprintMemoCapacity;
        return true;
    }

    /// <summary>Restricts local identity to the auto and engine UBO resolver paths.</summary>
    private bool IsKnownLocalDynamicUniformBinding(
        VkRenderProgram program,
        DescriptorBindingInfo binding,
        out bool isAutoUniform)
    {
        isAutoUniform = false;
        string name = binding.Name ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (TryResolveCachedBufferByName(name, out _))
                return false;

            XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is not null &&
                (TryResolvePipelineResourceDataBuffer(pipeline, name, binding.DescriptorType, out _) ||
                 TryResolvePipelineResourceDataBuffer(
                     pipeline,
                     TrimDescriptorBufferSuffix(name),
                     binding.DescriptorType,
                     out _)))
                return false;
        }

        isAutoUniform = program.TryGetAutoUniformBlock(binding.Set, binding.Binding, out _);
        return isAutoUniform || GetEngineUniformSize(name) != 0;
    }

    /// <summary>Checks that no newer ambient buffer source bypasses the UBO resolver.</summary>
    private bool LocalDynamicUniformSourcesRemainEligible(
        VkRenderProgram program,
        IReadOnlyList<DescriptorBindingInfo> bindings,
        uint activeSetMask,
        ComputeDispatchSnapshot bindingSnapshot)
    {
        int localBindingCount = 0;
        for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
        {
            DescriptorBindingInfo binding = bindings[bindingIndex];
            if (binding.Set >= 32 || (activeSetMask & (1u << (int)binding.Set)) == 0)
                continue;
            if (binding.DescriptorType != DescriptorType.UniformBufferDynamic ||
                VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding) != 1 ||
                bindingSnapshot.Buffers.ContainsKey(binding.Binding) ||
                !IsKnownLocalDynamicUniformBinding(program, binding, out _))
                return false;
            localBindingCount++;
        }

        return localBindingCount != 0;
    }
}
