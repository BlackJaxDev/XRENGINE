using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDescriptorManager
{
    /// <summary>
    /// The number of frames to wait before retiring a global material texture descriptor slot.
    /// </summary>
    private const ulong GlobalMaterialTextureRetireDelayFrames = 300ul;

    private object _globalMaterialTextureTableLock => BindlessMaterialTextures.Sync;
    private Dictionary<XRTexture, uint> _globalMaterialTextureDescriptorSlotsByTexture => BindlessMaterialTextures.SlotsByTexture;
    private Queue<uint> _freeGlobalMaterialTextureDescriptorSlots => BindlessMaterialTextures.FreeSlots;
    private List<uint> _dirtyGlobalMaterialTextureDescriptorSlots => BindlessMaterialTextures.DirtySlots;
    private ref MaterialTextureDescriptorSlot[] _globalMaterialTextureDescriptorSlots => ref BindlessMaterialTextures.Slots;
    private ref DescriptorSetLayout _globalMaterialTextureDescriptorSetLayout => ref BindlessMaterialTextures.SetLayout;
    private ref DescriptorPool _globalMaterialTextureDescriptorPool => ref BindlessMaterialTextures.Pool;
    private ref DescriptorSet _globalMaterialTextureDescriptorSet => ref BindlessMaterialTextures.Set;
    private ref uint _globalMaterialTextureDescriptorCapacity => ref BindlessMaterialTextures.Capacity;
    private ref uint _nextGlobalMaterialTextureDescriptorSlot => ref BindlessMaterialTextures.NextSlot;
    private ref bool _globalMaterialTextureDescriptorSetUsesUpdateAfterBind => ref BindlessMaterialTextures.UsesUpdateAfterBind;
    private ref bool _globalMaterialTextureDescriptorSetUsesVariableDescriptorCount => ref BindlessMaterialTextures.UsesVariableDescriptorCount;
    private ref VkRenderProgram? _globalMaterialTextureDescriptorScopeProgram => ref BindlessMaterialTextures.ScopeProgram;
    private ref string _globalMaterialTextureDescriptorScopeConsumer => ref BindlessMaterialTextures.ScopeConsumer;
    private ref ulong _globalMaterialTextureDescriptorWritesTotal => ref BindlessMaterialTextures.WritesTotal;
    private ref ulong _globalMaterialTextureDescriptorWritesLastFlush => ref BindlessMaterialTextures.WritesLastFlush;
    private ref ulong _globalMaterialTextureDescriptorSlotRetirementsTotal => ref BindlessMaterialTextures.SlotRetirementsTotal;
    private ref ulong _globalMaterialTextureDescriptorFallbackReferencesTotal => ref BindlessMaterialTextures.FallbackReferencesTotal;
    private ref VulkanBindlessMaterialCapability _bindlessMaterialCapability => ref BindlessMaterialTextures.Capability;

    /// <summary>
    /// Gets the current bindless material capability of the Vulkan renderer.
    /// </summary>
    public VulkanBindlessMaterialCapability BindlessMaterialCapability => RefreshBindlessMaterialCapability();

    /// <summary>
    /// Gets a value indicating whether the Vulkan renderer supports the bindless material table shader.
    /// </summary>
    public bool SupportsVulkanBindlessMaterialTableShader
        => RefreshBindlessMaterialCapability().Tier >= EVulkanBindlessMaterialCapabilityTier.BindlessMaterialTableShaderReady;

    /// <summary>
    /// Gets the capacity of the global material texture descriptor table.
    /// </summary>
    public uint GlobalMaterialTextureDescriptorCapacity
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorCapacity;
        }
    }

    /// <summary>
    /// Gets the number of global material texture descriptors currently in use.
    /// </summary>
    public uint GlobalMaterialTextureDescriptorsUsed
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorSet.Handle == 0
                    ? 0u
                    : (uint)_globalMaterialTextureDescriptorSlotsByTexture.Count + 1u;
        }
    }

    /// <summary>
    /// Gets the number of global material texture descriptors that are marked as dirty.
    /// </summary>
    public uint GlobalMaterialTextureDescriptorsDirty
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return (uint)_dirtyGlobalMaterialTextureDescriptorSlots.Count;
        }
    }

    /// <summary>
    /// Gets the total number of writes performed to the global material texture descriptor table.
    /// </summary>
    public ulong GlobalMaterialTextureDescriptorWritesTotal
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorWritesTotal;
        }
    }

    /// <summary>
    /// Gets the number of writes to the global material texture descriptor table since the last flush.
    /// </summary>
    public ulong GlobalMaterialTextureDescriptorWritesLastFlush
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorWritesLastFlush;
        }
    }

    /// <summary>
    /// Gets the total number of global material texture descriptor slot retirements.
    /// </summary>
    public ulong GlobalMaterialTextureDescriptorSlotRetirementsTotal
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorSlotRetirementsTotal;
        }
    }

    /// <summary>
    /// Gets the total number of fallback references for the global material texture descriptor table.
    /// </summary>
    public ulong GlobalMaterialTextureDescriptorFallbackReferencesTotal
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorFallbackReferencesTotal;
        }
    }

    /// <summary>
    /// Resolves a material texture descriptor and reports whether its table slot is published for drawing.
    /// </summary>
    /// <param name="texture">The material texture to resolve.</param>
    /// <param name="semantic">The semantic associated with the material texture.</param>
    /// <returns>A typed readiness result. Only <see cref="Materials.EMaterialTextureReferenceStatus.Ready"/> is draw-safe.</returns>
    internal Materials.MaterialTextureReferenceResolution ResolveMaterialTextureDescriptorReference(
        XRTexture? texture,
        string semantic)
    {
        if (texture is null)
        {
            const string reason = "Material texture is null.";
            RecordGlobalMaterialTextureFallback(semantic, reason);
            return Materials.MaterialTextureReferenceResolution.Failed(reason);
        }

        if (!TryResolveMaterialTextureDescriptor(texture, semantic, out DescriptorImageInfo imageInfo, out string descriptorReason))
        {
            RecordGlobalMaterialTextureFallback(semantic, descriptorReason);
            return Materials.MaterialTextureReferenceResolution.Pending(descriptorReason);
        }

        if (!TryEnsureGlobalMaterialTextureDescriptorTable(out string tableReason))
        {
            if (VulkanFeatureProfile.RequireBindlessMaterialTable)
                RecordGlobalMaterialTextureBindingFailure(semantic, tableReason, skippedDraw: true);
            else
                RecordGlobalMaterialTextureFallback(semantic, tableReason);

            return Materials.MaterialTextureReferenceResolution.Unsupported(tableReason);
        }

        uint descriptorIndex;
        bool pendingPublication;
        uint slotGeneration;
        lock (_globalMaterialTextureTableLock)
        {
            if (!_globalMaterialTextureDescriptorSlotsByTexture.TryGetValue(texture, out descriptorIndex) &&
                !TryAllocateGlobalMaterialTextureDescriptorSlot(texture, out descriptorIndex, out tableReason))
            {
                if (VulkanFeatureProfile.RequireBindlessMaterialTable)
                    RecordGlobalMaterialTextureBindingFailure(semantic, tableReason, skippedDraw: true);
                else
                    RecordGlobalMaterialTextureFallback(semantic, tableReason);

                return Materials.MaterialTextureReferenceResolution.Failed(tableReason);
            }

            ref MaterialTextureDescriptorSlot slot = ref _globalMaterialTextureDescriptorSlots[descriptorIndex];
            slot.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            slot.PendingRetirement = false;
            slot.RetireAfterFrameId = 0ul;

            if (slot.ImageInfo.ImageView.Handle != imageInfo.ImageView.Handle ||
                slot.ImageInfo.Sampler.Handle != imageInfo.Sampler.Handle ||
                slot.ImageInfo.ImageLayout != imageInfo.ImageLayout)
            {
                slot.ImageInfo = imageInfo;
                slot.Generation++;
                MarkGlobalMaterialTextureDescriptorSlotDirty(descriptorIndex);
            }

            pendingPublication = slot.Dirty;
            slotGeneration = slot.Generation;
        }

        if (descriptorIndex == 0u)
            return Materials.MaterialTextureReferenceResolution.Failed(
                "Descriptor index zero is reserved for shader fallback and is never a resident material reference.");

        Materials.GPUMaterialTextureReference reference =
            Materials.GPUMaterialTextureReference.FromVulkanDescriptorIndex(descriptorIndex);
        return pendingPublication
            ? new Materials.MaterialTextureReferenceResolution(
                Materials.EMaterialTextureReferenceStatus.Pending,
                reference,
                slotGeneration,
                "Material texture descriptor update is pending publication.")
            : Materials.MaterialTextureReferenceResolution.Ready(reference, slotGeneration);
    }
    /// <summary>
    /// Ensures that the global material texture descriptor table is available and ready for use.
    /// </summary>
    /// <param name="reason">The reason why the descriptor table could not be ensured, if applicable.</param>
    /// <returns>True if the global material texture descriptor table is available and ready for use; otherwise, false.</returns>
    internal bool TryEnsureGlobalMaterialTextureDescriptorTable(out string reason)
    {
        reason = string.Empty;

        if (!VulkanFeatureProfile.EnableBindlessMaterialTable)
        {
            reason = "Vulkan bindless material table is disabled by profile or setting.";
            RefreshBindlessMaterialCapability(reason);
            return false;
        }

        if (!SupportsDescriptorIndexing ||
            !DeviceContext.MutableCapabilities._supportsRuntimeDescriptorArray ||
            !DeviceContext.MutableCapabilities._supportsDescriptorBindingPartiallyBound ||
            !DeviceContext.MutableCapabilities._supportsDescriptorBindingUpdateAfterBind)
        {
            reason = FormatBindlessDescriptorIndexingUnavailableReason();
            RefreshBindlessMaterialCapability(reason);
            return false;
        }

        lock (_globalMaterialTextureTableLock)
        {
            if (_globalMaterialTextureDescriptorSet.Handle != 0)
            {
                RefreshBindlessMaterialCapability();
                return true;
            }

            _globalMaterialTextureDescriptorCapacity = ResolveGlobalMaterialTextureDescriptorCapacity();
            if (_globalMaterialTextureDescriptorCapacity <= 1u)
            {
                reason = $"Device sampled-image descriptor limits cannot reserve a bindless material table (capacity={_globalMaterialTextureDescriptorCapacity}).";
                RefreshBindlessMaterialCapability(reason);
                return false;
            }

            DescriptorSetLayoutBinding binding = new()
            {
                Binding = VulkanBindlessMaterialDescriptors.TextureArrayBinding,
                DescriptorType = DescriptorType.CombinedImageSampler,
                // The program layout declares the full runtime-array maximum.
                // The descriptor-set allocation below supplies the smaller
                // device-clamped live count through variableDescriptorCount.
                DescriptorCount = VulkanBindlessMaterialDescriptors.MaxTextureDescriptorCount,
                StageFlags = ShaderStageFlags.FragmentBit,
            };

            DescriptorSetLayoutBinding[] bindings = [binding];
            if (!ResourceRuntime.Descriptors.TryAcquireCachedDescriptorSetLayout(
                    VulkanBindlessMaterialDescriptors.TextureArraySet,
                    bindings,
                    out _globalMaterialTextureDescriptorSetLayout,
                    out _globalMaterialTextureDescriptorSetUsesUpdateAfterBind,
                    out _globalMaterialTextureDescriptorSetUsesVariableDescriptorCount))
            {
                reason = "Failed to create global material texture descriptor-set layout.";
                RefreshBindlessMaterialCapability(reason);
                return false;
            }

            DescriptorPoolSize poolSize = new()
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = _globalMaterialTextureDescriptorCapacity,
            };

            DescriptorPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = _globalMaterialTextureDescriptorSetUsesUpdateAfterBind
                    ? DescriptorPoolCreateFlags.UpdateAfterBindBit
                    : 0,
                PoolSizeCount = 1u,
                PPoolSizes = &poolSize,
                MaxSets = 1u,
            };

            if (Api.CreateDescriptorPool(DeviceContext.Device, ref poolInfo, null, out _globalMaterialTextureDescriptorPool) != Result.Success)
            {
                reason = "Failed to create global material texture descriptor pool.";
                ResourceRuntime.Descriptors.ReleaseCachedDescriptorSetLayout(_globalMaterialTextureDescriptorSetLayout);
                _globalMaterialTextureDescriptorSetLayout = default;
                RefreshBindlessMaterialCapability(reason);
                return false;
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();

            DescriptorSetLayout layout = _globalMaterialTextureDescriptorSetLayout;
            DescriptorSet descriptorSet = default;
            uint variableDescriptorCount = _globalMaterialTextureDescriptorCapacity;
            DescriptorSetVariableDescriptorCountAllocateInfo variableDescriptorCountInfo = new()
            {
                SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
                DescriptorSetCount = 1u,
                PDescriptorCounts = &variableDescriptorCount,
            };

            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                PNext = _globalMaterialTextureDescriptorSetUsesVariableDescriptorCount ? &variableDescriptorCountInfo : null,
                DescriptorPool = _globalMaterialTextureDescriptorPool,
                DescriptorSetCount = 1u,
                PSetLayouts = &layout,
            };

            if (Api.AllocateDescriptorSets(DeviceContext.Device, ref allocInfo, &descriptorSet) != Result.Success)
            {
                reason = "Failed to allocate global material texture descriptor set.";
                DestroyGlobalMaterialTextureDescriptorTable();
                RefreshBindlessMaterialCapability(reason);
                return false;
            }

            _globalMaterialTextureDescriptorSet = descriptorSet;
            ResourceRuntime.DescriptorLifetime.RegisterDescriptorSet(
                _globalMaterialTextureDescriptorPool,
                descriptorSet,
                _globalMaterialTextureDescriptorSetUsesUpdateAfterBind,
                "GlobalMaterialTexture.DescriptorSet");
            SetDebugDescriptorSetName(_globalMaterialTextureDescriptorSet, "GlobalMaterialTexture.DescriptorSet");
            RecordVulkanDescriptorTableGeneration("GlobalMaterialTextureDescriptorSet.Allocated");
            _globalMaterialTextureDescriptorSlots = new MaterialTextureDescriptorSlot[_globalMaterialTextureDescriptorCapacity];
            _nextGlobalMaterialTextureDescriptorSlot = 1u;
            _freeGlobalMaterialTextureDescriptorSlots.Clear();
            _globalMaterialTextureDescriptorSlotsByTexture.Clear();
            _dirtyGlobalMaterialTextureDescriptorSlots.Clear();

            DescriptorImageInfo fallbackInfo = BackendContext.FallbackTexture.GetImageInfo(
                DescriptorType.CombinedImageSampler,
                ImageViewType.Type2D);
            if (fallbackInfo.ImageView.Handle == 0 || fallbackInfo.Sampler.Handle == 0)
            {
                reason = "Failed to create placeholder descriptor for global material texture slot 0.";
                DestroyGlobalMaterialTextureDescriptorTable();
                RefreshBindlessMaterialCapability(reason);
                return false;
            }

            _globalMaterialTextureDescriptorSlots[0] = new MaterialTextureDescriptorSlot
            {
                ImageInfo = fallbackInfo,
                Generation = 1u,
                LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId,
            };
            MarkGlobalMaterialTextureDescriptorSlotDirty(0u);
            FlushGlobalMaterialTextureDescriptorUpdatesLocked();
            reason = string.Empty;
            RefreshBindlessMaterialCapability();
            Debug.Vulkan(
                "[Vulkan] Global material texture descriptor table ready: set={0} binding={1} capacity={2} updateAfterBind={3} variableCount={4}.",
                VulkanBindlessMaterialDescriptors.TextureArraySet,
                VulkanBindlessMaterialDescriptors.TextureArrayBinding,
                _globalMaterialTextureDescriptorCapacity,
                _globalMaterialTextureDescriptorSetUsesUpdateAfterBind,
                _globalMaterialTextureDescriptorSetUsesVariableDescriptorCount);
            return true;
        }
    }

    /// <summary>
    /// Flushes any pending updates to the global material texture descriptor table.
    /// This ensures that any changes to the descriptor slots are applied to the Vulkan descriptor set.
    /// </summary>
    internal void FlushGlobalMaterialTextureDescriptorUpdates()
    {
        lock (_globalMaterialTextureTableLock)
            FlushGlobalMaterialTextureDescriptorUpdatesLocked();
    }

    /// <summary>
    /// Begins a scope for binding the global material texture descriptor set for the specified render program and consumer.
    /// </summary>
    /// <param name="program">The render program for which the descriptor set scope is being begun.</param>
    /// <param name="consumer">The consumer requesting the descriptor set binding.</param>
    /// <returns>True if the scope was successfully begun; otherwise, false.</returns>
    internal bool BeginGlobalMaterialTextureDescriptorScope(XRRenderProgram program, string consumer)
    {
        if (!TryEnsureGlobalMaterialTextureDescriptorTable(out string reason))
        {
            RecordGlobalMaterialTextureBindingFailure(consumer, reason, skippedDraw: true);
            return false;
        }

        VkRenderProgram? vkProgram = GenericToAPI<VkRenderProgram>(program);
        if (vkProgram is null)
        {
            RecordGlobalMaterialTextureBindingFailure(consumer, "program has no Vulkan API object", skippedDraw: true);
            return false;
        }

        _globalMaterialTextureDescriptorScopeProgram = vkProgram;
        _globalMaterialTextureDescriptorScopeConsumer = consumer;
        return true;
    }

    /// <summary>
    /// Ends the scope for binding the global material texture descriptor set for the specified render program.
    /// </summary>
    /// <param name="program">The render program for which the descriptor set scope is being ended.</param>
    internal void EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program)
    {
        VkRenderProgram? vkProgram = GenericToAPI<VkRenderProgram>(program);
        if (vkProgram is null || ReferenceEquals(vkProgram, _globalMaterialTextureDescriptorScopeProgram))
        {
            _globalMaterialTextureDescriptorScopeProgram = null;
            _globalMaterialTextureDescriptorScopeConsumer = string.Empty;
        }
    }

    /// <summary>
    /// Captures the current global material texture descriptor binding for use in the next frame.
    /// </summary>
    /// <returns>The captured global material texture descriptor binding, or null if no binding is currently active.</returns>
    internal VulkanBindlessMaterialDescriptorBinding? CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp()
    {
        VkRenderProgram? program = _globalMaterialTextureDescriptorScopeProgram;
        return program is null 
            ? null 
            : new VulkanBindlessMaterialDescriptorBinding(
                program, 
                _globalMaterialTextureDescriptorScopeConsumer);
    }

    /// <summary>
    /// Tries to bind the global material texture descriptor set for the specified render program.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to which the descriptor set should be bound.</param>
    /// <param name="program">The render program for which the descriptor set is being bound.</param>
    /// <param name="consumer">The consumer requesting the binding.</param>
    /// <returns>True if the descriptor set was successfully bound; otherwise, false.</returns>
    internal bool TryBindGlobalMaterialTextureDescriptorSet(
        CommandBuffer commandBuffer,
        VkRenderProgram program,
        string consumer)
    {
        if (program.PipelineLayout.Handle == 0)
        {
            RecordGlobalMaterialTextureBindingFailure(consumer, "program pipeline layout is not ready", skippedDraw: true);
            return false;
        }

        if (program.DescriptorSetLayouts.Count <= VulkanBindlessMaterialDescriptors.TextureArraySet)
        {
            RecordGlobalMaterialTextureBindingFailure(consumer, "program layout does not include the bindless material texture descriptor set", skippedDraw: true);
            return false;
        }

        if (!TryEnsureGlobalMaterialTextureDescriptorTable(out string reason))
        {
            RecordGlobalMaterialTextureBindingFailure(consumer, reason, skippedDraw: true);
            return false;
        }

        DescriptorSetLayout programMaterialLayout =
            program.DescriptorSetLayouts[(int)VulkanBindlessMaterialDescriptors.TextureArraySet];
        if (programMaterialLayout.Handle != _globalMaterialTextureDescriptorSetLayout.Handle)
        {
            RecordGlobalMaterialTextureBindingFailure(
                consumer,
                "program material descriptor layout is incompatible with the shared bindless texture table; set 2 must contain only XR_BindlessMaterialTextures",
                skippedDraw: true);
            return false;
        }

        FlushGlobalMaterialTextureDescriptorUpdates();
        Span<DescriptorSet> sets = stackalloc DescriptorSet[1];
        sets[0] = _globalMaterialTextureDescriptorSet;
        CommandRuntime.BindDescriptorSetsTracked(
            commandBuffer,
            PipelineBindPoint.Graphics,
            program.PipelineLayout,
            VulkanBindlessMaterialDescriptors.TextureArraySet,
            sets,
            ReadOnlySpan<uint>.Empty);
        return true;
    }

    /// <summary>
    /// Tries to resolve the descriptor for the specified material texture, ensuring it is ready for use in the bindless material texture array.
    /// </summary>
    /// <param name="texture">The material texture to resolve the descriptor for.</param>
    /// <param name="semantic">The semantic associated with the material texture.</param>
    /// <param name="imageInfo">The resolved descriptor image information for the texture.</param>
    /// <param name="reason">The reason for failure if the descriptor could not be resolved.</param>
    /// <returns>True if the descriptor was successfully resolved; otherwise, false.</returns>
    private bool TryResolveMaterialTextureDescriptor(
        XRTexture texture,
        string semantic,
        out DescriptorImageInfo imageInfo,
        out string reason)
    {
        imageInfo = default;
        reason = string.Empty;

        bool allowSynchronousTextureUpload = AllowSynchronousResourceUploads;
        if (BackendContext.GetOrCreateAPIRenderObject(texture, generateNow: allowSynchronousTextureUpload) is not IVkImageDescriptorSource source)
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' has no Vulkan image descriptor source.";
            return false;
        }

        if (!source.TryEnsureDescriptorReadyForUse($"bindless material texture '{semantic}'", allowSynchronousTextureUpload))
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' descriptor is not ready for Vulkan sampling.";
            return false;
        }

        if ((source.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' is missing VK_IMAGE_USAGE_SAMPLED_BIT.";
            return false;
        }

        ImageView descriptorView = source.DescriptorViewType == ImageViewType.Type2D
            ? source.DescriptorView
            : source.GetDescriptorView(ImageViewType.Type2D);
        if (descriptorView.Handle == 0)
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' cannot provide a 2D view for material semantic '{semantic}'.";
            return false;
        }

        if (!BackendContext.Images.IsAvailableForDescriptor(descriptorView))
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' references a retired Vulkan image view for material semantic '{semantic}'.";
            return false;
        }

        Sampler descriptorSampler = source.DescriptorSampler;
        if (descriptorSampler.Handle != 0 && !ResourceRuntime.Descriptors.IsLiveSampler(descriptorSampler))
            descriptorSampler = default;

        if (descriptorSampler.Handle == 0)
            descriptorSampler = BackendContext.FallbackTexture.GetSampler();

        if (descriptorSampler.Handle == 0 || !ResourceRuntime.Descriptors.IsLiveSampler(descriptorSampler))
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' has no sampler and the placeholder sampler is unavailable.";
            return false;
        }

        imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ResourceRuntime.Descriptors.ResolveDescriptorImageLayout(
                source,
                DescriptorType.CombinedImageSampler),
            ImageView = descriptorView,
            Sampler = descriptorSampler,
        };
        return true;
    }

    /// <summary>
    /// Tries to allocate a global material texture descriptor slot for the specified texture.
    /// </summary>
    /// <param name="texture">The material texture to allocate a descriptor slot for.</param>
    /// <param name="descriptorIndex">The allocated descriptor slot index if successful.</param>
    /// <param name="reason">The reason for failure if the allocation was unsuccessful.</param>
    /// <returns>True if a descriptor slot was successfully allocated; otherwise, false.</returns>
    private bool TryAllocateGlobalMaterialTextureDescriptorSlot(
        XRTexture texture,
        out uint descriptorIndex,
        out string reason)
    {
        reason = string.Empty;
        descriptorIndex = _freeGlobalMaterialTextureDescriptorSlots.Count > 0
            ? _freeGlobalMaterialTextureDescriptorSlots.Dequeue()
            : _nextGlobalMaterialTextureDescriptorSlot++;

        if (descriptorIndex == 0u || descriptorIndex >= _globalMaterialTextureDescriptorCapacity)
        {
            reason = $"Global material texture descriptor table is full (capacity={_globalMaterialTextureDescriptorCapacity}).";
            descriptorIndex = 0u;
            return false;
        }

        ref MaterialTextureDescriptorSlot slot = ref _globalMaterialTextureDescriptorSlots[descriptorIndex];
        slot.Texture = texture;
        slot.Generation++;
        slot.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        slot.PendingRetirement = false;
        slot.RetireAfterFrameId = 0ul;
        _globalMaterialTextureDescriptorSlotsByTexture[texture] = descriptorIndex;
        return true;
    }

    /// <summary>
    /// Flushes any pending updates to the global material texture descriptor table.
    /// </summary>
    private void FlushGlobalMaterialTextureDescriptorUpdatesLocked()
    {
        if (_globalMaterialTextureDescriptorSet.Handle == 0)
            return;

        RetireUnusedGlobalMaterialTextureDescriptorSlotsLocked(RuntimeEngine.Rendering.State.RenderFrameId);
        if (_dirtyGlobalMaterialTextureDescriptorSlots.Count == 0)
        {
            _globalMaterialTextureDescriptorWritesLastFlush = 0ul;
            return;
        }

        int dirtyCount = _dirtyGlobalMaterialTextureDescriptorSlots.Count;
        DescriptorImageInfo[] imageInfos = ArrayPool<DescriptorImageInfo>.Shared.Rent(dirtyCount);
        WriteDescriptorSet[] writes = ArrayPool<WriteDescriptorSet>.Shared.Rent(dirtyCount);

        try
        {
            fixed (DescriptorImageInfo* imageInfoPtr = imageInfos)
            fixed (WriteDescriptorSet* writePtr = writes)
            {
                for (int i = 0; i < dirtyCount; i++)
                {
                    uint descriptorIndex = _dirtyGlobalMaterialTextureDescriptorSlots[i];
                    imageInfos[i] = _globalMaterialTextureDescriptorSlots[descriptorIndex].ImageInfo;
                    writes[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = _globalMaterialTextureDescriptorSet,
                        DstBinding = VulkanBindlessMaterialDescriptors.TextureArrayBinding,
                        DstArrayElement = descriptorIndex,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        DescriptorCount = 1u,
                        PImageInfo = imageInfoPtr + i,
                    };
                }

                ResourceRuntime.DescriptorLifetime.UpdateDescriptorSets((uint)dirtyCount, writePtr);
                RecordVulkanDescriptorTableGeneration("GlobalMaterialTextureDescriptorSet.Update");
            }
        }
        finally
        {
            ArrayPool<DescriptorImageInfo>.Shared.Return(imageInfos);
            ArrayPool<WriteDescriptorSet>.Shared.Return(writes);
        }

        for (int i = 0; i < dirtyCount; i++)
            _globalMaterialTextureDescriptorSlots[_dirtyGlobalMaterialTextureDescriptorSlots[i]].Dirty = false;

        _globalMaterialTextureDescriptorWritesLastFlush = (ulong)dirtyCount;
        _globalMaterialTextureDescriptorWritesTotal += (ulong)dirtyCount;
        _dirtyGlobalMaterialTextureDescriptorSlots.Clear();
    }

    /// <summary>
    /// Retires any global material texture descriptor slots that have not been used for a specified number of frames.
    /// </summary>
    /// <param name="frameId">The current frame ID used to determine which descriptor slots should be retired.</param>
    private void RetireUnusedGlobalMaterialTextureDescriptorSlotsLocked(ulong frameId)
    {
        if (_globalMaterialTextureDescriptorSlots.Length == 0)
            return;

        DescriptorImageInfo fallbackInfo = _globalMaterialTextureDescriptorSlots[0].ImageInfo;
        ulong retiredCount = 0ul;
        for (uint i = 1u; i < _globalMaterialTextureDescriptorSlots.Length; i++)
        {
            ref MaterialTextureDescriptorSlot slot = ref _globalMaterialTextureDescriptorSlots[i];
            if (slot.Texture is null)
                continue;

            if (slot.LastUsedFrameId + GlobalMaterialTextureRetireDelayFrames > frameId)
                continue;

            _globalMaterialTextureDescriptorSlotsByTexture.Remove(slot.Texture);
            slot.Texture = null;
            slot.ImageInfo = fallbackInfo;
            slot.Generation++;
            slot.PendingRetirement = true;
            slot.RetireAfterFrameId = frameId;
            MarkGlobalMaterialTextureDescriptorSlotDirty(i);
            _freeGlobalMaterialTextureDescriptorSlots.Enqueue(i);
            retiredCount++;
        }

        _globalMaterialTextureDescriptorSlotRetirementsTotal += retiredCount;
    }

    /// <summary>
    /// Marks the specified global material texture descriptor slot as dirty, indicating that it needs to be updated in the descriptor table.
    /// </summary>
    /// <param name="descriptorIndex">The index of the descriptor slot to mark as dirty.</param>
    private void MarkGlobalMaterialTextureDescriptorSlotDirty(uint descriptorIndex)
    {
        if (descriptorIndex >= _globalMaterialTextureDescriptorSlots.Length)
            return;

        ref MaterialTextureDescriptorSlot slot = ref _globalMaterialTextureDescriptorSlots[descriptorIndex];
        if (slot.Dirty)
            return;

        slot.Dirty = true;
        _dirtyGlobalMaterialTextureDescriptorSlots.Add(descriptorIndex);
    }

    /// <summary>
    /// Resolves the capacity of the global material texture descriptor table based on the physical device limits and Vulkan bindless material descriptor constraints.
    /// </summary>
    /// <returns>The resolved capacity of the global material texture descriptor table.</returns>
    private uint ResolveGlobalMaterialTextureDescriptorCapacity()
    {
        Api.GetPhysicalDeviceProperties(DeviceContext.PhysicalDevice, out PhysicalDeviceProperties properties);
        uint capacity = VulkanBindlessMaterialDescriptors.MaxTextureDescriptorCount;
        uint setLimit = properties.Limits.MaxDescriptorSetSampledImages;
        uint stageLimit = properties.Limits.MaxPerStageDescriptorSampledImages;

        if (setLimit > 0u)
            capacity = Math.Min(capacity, setLimit);
        if (stageLimit > 0u)
            capacity = Math.Min(capacity, stageLimit);

        return Math.Max(1u, capacity);
    }

    /// <summary>
    /// Refreshes the Vulkan bindless material capability, taking into account the current state of the global material texture descriptor table, shader readiness, and draw path readiness.
    /// </summary>
    /// <param name="overrideReason">An optional reason to override the default capability evaluation.</param>
    /// <returns>The refreshed Vulkan bindless material capability.</returns>
    internal VulkanBindlessMaterialCapability RefreshBindlessMaterialCapability(string? overrideReason = null)
    {
        EVulkanBindlessMaterialMode mode = VulkanFeatureProfile.ActiveBindlessMaterialMode;
        EVulkanBindlessMaterialCapabilityTier tier = EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingUnavailable;
        bool tableReady = _globalMaterialTextureDescriptorSet.Handle != 0;
        bool shaderReady = tableReady && VulkanFeatureProfile.EnableBindlessMaterialTable;
        bool drawPathReady = shaderReady && VulkanFeatureProfile.EnableGpuRenderDispatch;
        string reason = overrideReason ?? string.Empty;

        if (mode == EVulkanBindlessMaterialMode.Disabled)
            reason = "Vulkan bindless material mode is Disabled.";
        else if (!SupportsDescriptorIndexing ||
                 !DeviceContext.MutableCapabilities._supportsRuntimeDescriptorArray ||
                 !DeviceContext.MutableCapabilities._supportsDescriptorBindingPartiallyBound ||
                 !DeviceContext.MutableCapabilities._supportsDescriptorBindingUpdateAfterBind)
            reason = FormatBindlessDescriptorIndexingUnavailableReason();
        else
            tier = EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady;

        if (tier >= EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady && tableReady)
            tier = EVulkanBindlessMaterialCapabilityTier.GlobalMaterialTextureTableReady;

        if (tier >= EVulkanBindlessMaterialCapabilityTier.GlobalMaterialTextureTableReady && shaderReady)
            tier = EVulkanBindlessMaterialCapabilityTier.BindlessMaterialTableShaderReady;

        if (tier >= EVulkanBindlessMaterialCapabilityTier.BindlessMaterialTableShaderReady && drawPathReady)
            tier = EVulkanBindlessMaterialCapabilityTier.BindlessMaterialDrawPathReady;
        else if (tier >= EVulkanBindlessMaterialCapabilityTier.BindlessMaterialTableShaderReady &&
                 string.IsNullOrWhiteSpace(reason))
            reason = "Vulkan GPU render dispatch is disabled by the active Vulkan feature profile or runtime settings.";

        _bindlessMaterialCapability = new VulkanBindlessMaterialCapability(
            mode,
            tier,
            SupportsDescriptorIndexing,
            DeviceContext.MutableCapabilities._supportsRuntimeDescriptorArray,
            DeviceContext.MutableCapabilities._supportsDescriptorBindingPartiallyBound,
            DeviceContext.MutableCapabilities._supportsDescriptorBindingUpdateAfterBind,
            _globalMaterialTextureDescriptorCapacity,
            tableReady,
            shaderReady,
            drawPathReady,
            reason);

        return _bindlessMaterialCapability;
    }

    /// <summary>
    /// Validates that the required Vulkan bindless material capability is available, throwing an exception if the prerequisites are not met.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the required Vulkan bindless material capability is not available.</exception>
    internal void ValidateRequiredVulkanBindlessMaterialCapability()
    {
        VulkanBindlessMaterialCapability capability = RefreshBindlessMaterialCapability();
        if (capability.Mode != EVulkanBindlessMaterialMode.Required)
            return;

        if (capability.Tier >= EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady)
            return;

        throw new InvalidOperationException(
            $"Vulkan bindless material mode is Required but descriptor indexing prerequisites are unavailable. {capability.Reason}");
    }

    /// <summary>
    /// Formats a human-readable reason indicating why descriptor indexing prerequisites are unavailable.
    /// </summary>
    /// <returns>A formatted string explaining why descriptor indexing prerequisites are unavailable.</returns>
    private string FormatBindlessDescriptorIndexingUnavailableReason()
        => $"Descriptor indexing prerequisites unavailable (descriptorIndexing={SupportsDescriptorIndexing}, runtimeArray={DeviceContext.MutableCapabilities._supportsRuntimeDescriptorArray}, partiallyBound={DeviceContext.MutableCapabilities._supportsDescriptorBindingPartiallyBound}, updateAfterBind={DeviceContext.MutableCapabilities._supportsDescriptorBindingUpdateAfterBind}).";

    /// <summary>
    /// Records a fallback for the global material texture table, typically used when a descriptor binding cannot be satisfied.
    /// </summary>
    /// <param name="semantic">The semantic name of the texture binding that caused the fallback.</param>
    /// <param name="reason">The reason for the fallback.</param>
    private void RecordGlobalMaterialTextureFallback(string semantic, string reason)
    {
        lock (_globalMaterialTextureTableLock)
            _globalMaterialTextureDescriptorFallbackReferencesTotal++;

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorFallback(
            "GlobalMaterialTextureTable",
            "sampled-image",
            string.IsNullOrWhiteSpace(semantic) ? VulkanBindlessMaterialDescriptors.TextureArrayBindingName : semantic,
            VulkanBindlessMaterialDescriptors.TextureArraySet,
            VulkanBindlessMaterialDescriptors.TextureArrayBinding);
    }

    /// <summary>
    /// Records a failure for the global material texture binding, typically used when a descriptor binding cannot be satisfied.
    /// </summary>
    /// <param name="semantic">The semantic name of the texture binding that caused the failure.</param>
    /// <param name="reason">The reason for the failure.</param>
    /// <param name="skippedDraw">Indicates whether the draw call was skipped due to the failure.</param>
    private static void RecordGlobalMaterialTextureBindingFailure(string semantic, string reason, bool skippedDraw)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
            "GlobalMaterialTextureTable",
            "sampled-image",
            string.IsNullOrWhiteSpace(semantic) ? VulkanBindlessMaterialDescriptors.TextureArrayBindingName : semantic,
            VulkanBindlessMaterialDescriptors.TextureArraySet,
            VulkanBindlessMaterialDescriptors.TextureArrayBinding,
            skippedDraw,
            skippedDispatch: false,
            reason);

    /// <summary>
    /// Destroys the global material texture descriptor table, releasing all associated resources and resetting the state.
    /// </summary>
    internal void DestroyGlobalMaterialTextureDescriptorTable()
    {
        lock (_globalMaterialTextureTableLock)
        {
            _globalMaterialTextureDescriptorSlotsByTexture.Clear();
            _freeGlobalMaterialTextureDescriptorSlots.Clear();
            _dirtyGlobalMaterialTextureDescriptorSlots.Clear();
            _globalMaterialTextureDescriptorSlots = [];
            _globalMaterialTextureDescriptorCapacity = 0u;
            _nextGlobalMaterialTextureDescriptorSlot = 1u;

            if (_globalMaterialTextureDescriptorPool.Handle != 0)
            {
                RetireDescriptorPool(_globalMaterialTextureDescriptorPool);
                _globalMaterialTextureDescriptorPool = default;
            }

            if (_globalMaterialTextureDescriptorSetLayout.Handle != 0)
            {
                ResourceRuntime.Descriptors.ReleaseCachedDescriptorSetLayout(_globalMaterialTextureDescriptorSetLayout);
                _globalMaterialTextureDescriptorSetLayout = default;
            }

            _globalMaterialTextureDescriptorSet = default;
            _globalMaterialTextureDescriptorSetUsesUpdateAfterBind = false;
            _globalMaterialTextureDescriptorSetUsesVariableDescriptorCount = false;
            RefreshBindlessMaterialCapability("Global material texture descriptor table destroyed.");
        }
    }
}
