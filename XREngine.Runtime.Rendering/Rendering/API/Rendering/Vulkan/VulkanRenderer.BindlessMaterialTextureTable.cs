using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const ulong GlobalMaterialTextureRetireDelayFrames = 300ul;

    private readonly object _globalMaterialTextureTableLock = new();
    private readonly Dictionary<XRTexture, uint> _globalMaterialTextureDescriptorSlotsByTexture = new(ReferenceTextureComparer.Instance);
    private readonly Queue<uint> _freeGlobalMaterialTextureDescriptorSlots = new();
    private readonly List<uint> _dirtyGlobalMaterialTextureDescriptorSlots = [];
    private MaterialTextureDescriptorSlot[] _globalMaterialTextureDescriptorSlots = [];
    private DescriptorSetLayout _globalMaterialTextureDescriptorSetLayout;
    private DescriptorPool _globalMaterialTextureDescriptorPool;
    private DescriptorSet _globalMaterialTextureDescriptorSet;
    private uint _globalMaterialTextureDescriptorCapacity;
    private uint _nextGlobalMaterialTextureDescriptorSlot = 1u;
    private bool _globalMaterialTextureDescriptorSetUsesUpdateAfterBind;
    private bool _globalMaterialTextureDescriptorSetUsesVariableDescriptorCount;
    private VkRenderProgram? _globalMaterialTextureDescriptorScopeProgram;
    private string _globalMaterialTextureDescriptorScopeConsumer = string.Empty;
    private ulong _globalMaterialTextureDescriptorWritesTotal;
    private ulong _globalMaterialTextureDescriptorWritesLastFlush;
    private ulong _globalMaterialTextureDescriptorSlotRetirementsTotal;
    private ulong _globalMaterialTextureDescriptorFallbackReferencesTotal;
    private VulkanBindlessMaterialCapability _bindlessMaterialCapability;

    public VulkanBindlessMaterialCapability BindlessMaterialCapability => RefreshBindlessMaterialCapability();

    public bool SupportsVulkanBindlessMaterialTableShader
        => RefreshBindlessMaterialCapability().Tier >= EVulkanBindlessMaterialCapabilityTier.BindlessMaterialTableShaderReady;

    public uint GlobalMaterialTextureDescriptorCapacity
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorCapacity;
        }
    }

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

    public uint GlobalMaterialTextureDescriptorsDirty
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return (uint)_dirtyGlobalMaterialTextureDescriptorSlots.Count;
        }
    }

    public ulong GlobalMaterialTextureDescriptorWritesTotal
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorWritesTotal;
        }
    }

    public ulong GlobalMaterialTextureDescriptorWritesLastFlush
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorWritesLastFlush;
        }
    }

    public ulong GlobalMaterialTextureDescriptorSlotRetirementsTotal
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorSlotRetirementsTotal;
        }
    }

    public ulong GlobalMaterialTextureDescriptorFallbackReferencesTotal
    {
        get
        {
            lock (_globalMaterialTextureTableLock)
                return _globalMaterialTextureDescriptorFallbackReferencesTotal;
        }
    }

    internal bool TryGetOrCreateMaterialTextureDescriptorIndex(
        XRTexture? texture,
        string semantic,
        out uint descriptorIndex,
        out bool usedFallback)
    {
        descriptorIndex = 0u;
        usedFallback = false;

        if (texture is null)
        {
            usedFallback = true;
            RecordGlobalMaterialTextureFallback(semantic, "texture is null");
            return true;
        }

        if (!TryResolveMaterialTextureDescriptor(texture, semantic, out DescriptorImageInfo imageInfo, out string descriptorReason))
        {
            usedFallback = true;
            RecordGlobalMaterialTextureFallback(semantic, descriptorReason);
            return true;
        }

        if (!TryEnsureGlobalMaterialTextureDescriptorTable(out string tableReason))
        {
            usedFallback = true;
            if (VulkanFeatureProfile.RequireBindlessMaterialTable)
            {
                RecordGlobalMaterialTextureBindingFailure(semantic, tableReason, skippedDraw: true);
                return false;
            }

            RecordGlobalMaterialTextureFallback(semantic, tableReason);
            return true;
        }

        lock (_globalMaterialTextureTableLock)
        {
            if (!_globalMaterialTextureDescriptorSlotsByTexture.TryGetValue(texture, out descriptorIndex))
            {
                if (!TryAllocateGlobalMaterialTextureDescriptorSlot(texture, out descriptorIndex, out tableReason))
                {
                    usedFallback = true;
                    if (VulkanFeatureProfile.RequireBindlessMaterialTable)
                    {
                        RecordGlobalMaterialTextureBindingFailure(semantic, tableReason, skippedDraw: true);
                        return false;
                    }

                    RecordGlobalMaterialTextureFallback(semantic, tableReason);
                    descriptorIndex = 0u;
                    return true;
                }
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
        }

        return true;
    }

    internal bool TryEnsureGlobalMaterialTextureDescriptorTable(out string reason)
    {
        reason = string.Empty;

        if (!VulkanFeatureProfile.EnableBindlessMaterialTable)
        {
            reason = "Vulkan bindless material table is disabled by profile or setting.";
            RefreshBindlessMaterialCapability(reason);
            return false;
        }

        if (!_supportsDescriptorIndexing ||
            !_supportsRuntimeDescriptorArray ||
            !_supportsDescriptorBindingPartiallyBound ||
            !_supportsDescriptorBindingUpdateAfterBind)
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
                DescriptorCount = _globalMaterialTextureDescriptorCapacity,
                StageFlags = ShaderStageFlags.FragmentBit,
            };

            DescriptorSetLayoutBinding[] bindings = [binding];
            if (!TryAcquireCachedDescriptorSetLayout(
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

            if (Api!.CreateDescriptorPool(device, ref poolInfo, null, out _globalMaterialTextureDescriptorPool) != Result.Success)
            {
                reason = "Failed to create global material texture descriptor pool.";
                ReleaseCachedDescriptorSetLayout(_globalMaterialTextureDescriptorSetLayout);
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

            if (Api.AllocateDescriptorSets(device, ref allocInfo, &descriptorSet) != Result.Success)
            {
                reason = "Failed to allocate global material texture descriptor set.";
                DestroyGlobalMaterialTextureDescriptorTable();
                RefreshBindlessMaterialCapability(reason);
                return false;
            }

            _globalMaterialTextureDescriptorSet = descriptorSet;
            _globalMaterialTextureDescriptorSlots = new MaterialTextureDescriptorSlot[_globalMaterialTextureDescriptorCapacity];
            _nextGlobalMaterialTextureDescriptorSlot = 1u;
            _freeGlobalMaterialTextureDescriptorSlots.Clear();
            _globalMaterialTextureDescriptorSlotsByTexture.Clear();
            _dirtyGlobalMaterialTextureDescriptorSlots.Clear();

            DescriptorImageInfo fallbackInfo = GetPlaceholderImageInfo(DescriptorType.CombinedImageSampler, ImageViewType.Type2D);
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

    internal void FlushGlobalMaterialTextureDescriptorUpdates()
    {
        lock (_globalMaterialTextureTableLock)
            FlushGlobalMaterialTextureDescriptorUpdatesLocked();
    }

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

    internal void EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program)
    {
        VkRenderProgram? vkProgram = GenericToAPI<VkRenderProgram>(program);
        if (vkProgram is null || ReferenceEquals(vkProgram, _globalMaterialTextureDescriptorScopeProgram))
        {
            _globalMaterialTextureDescriptorScopeProgram = null;
            _globalMaterialTextureDescriptorScopeConsumer = string.Empty;
        }
    }

    private VulkanBindlessMaterialDescriptorBinding? CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp()
    {
        VkRenderProgram? program = _globalMaterialTextureDescriptorScopeProgram;
        if (program is null)
            return null;

        return new VulkanBindlessMaterialDescriptorBinding(program, _globalMaterialTextureDescriptorScopeConsumer);
    }

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

        FlushGlobalMaterialTextureDescriptorUpdates();
        Span<DescriptorSet> sets = stackalloc DescriptorSet[1];
        sets[0] = _globalMaterialTextureDescriptorSet;
        BindDescriptorSetsTracked(
            commandBuffer,
            PipelineBindPoint.Graphics,
            program.PipelineLayout,
            VulkanBindlessMaterialDescriptors.TextureArraySet,
            sets,
            ReadOnlySpan<uint>.Empty);
        return true;
    }

    private bool TryResolveMaterialTextureDescriptor(
        XRTexture texture,
        string semantic,
        out DescriptorImageInfo imageInfo,
        out string reason)
    {
        imageInfo = default;
        reason = string.Empty;

        if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' has no Vulkan image descriptor source.";
            return false;
        }

        if (!source.IsDescriptorReady)
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

        Sampler descriptorSampler = source.DescriptorSampler;
        if (descriptorSampler.Handle != 0 && !IsLiveSampler(descriptorSampler))
            descriptorSampler = default;

        if (descriptorSampler.Handle == 0)
            descriptorSampler = GetPlaceholderSampler();

        if (descriptorSampler.Handle == 0 || !IsLiveSampler(descriptorSampler))
        {
            reason = $"Texture '{texture.Name ?? "<unnamed>"}' has no sampler and the placeholder sampler is unavailable.";
            return false;
        }

        imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ResolveDescriptorImageLayout(source, DescriptorType.CombinedImageSampler),
            ImageView = descriptorView,
            Sampler = descriptorSampler,
        };
        return true;
    }

    private bool TryAllocateGlobalMaterialTextureDescriptorSlot(
        XRTexture texture,
        out uint descriptorIndex,
        out string reason)
    {
        reason = string.Empty;
        descriptorIndex = 0u;

        if (_freeGlobalMaterialTextureDescriptorSlots.Count > 0)
        {
            descriptorIndex = _freeGlobalMaterialTextureDescriptorSlots.Dequeue();
        }
        else
        {
            descriptorIndex = _nextGlobalMaterialTextureDescriptorSlot++;
        }

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

                Api!.UpdateDescriptorSets(device, (uint)dirtyCount, writePtr, 0, null);
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

    private uint ResolveGlobalMaterialTextureDescriptorCapacity()
    {
        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        uint capacity = VulkanBindlessMaterialDescriptors.MaxTextureDescriptorCount;
        uint setLimit = properties.Limits.MaxDescriptorSetSampledImages;
        uint stageLimit = properties.Limits.MaxPerStageDescriptorSampledImages;

        if (setLimit > 0u)
            capacity = Math.Min(capacity, setLimit);
        if (stageLimit > 0u)
            capacity = Math.Min(capacity, stageLimit);

        return Math.Max(1u, capacity);
    }

    private VulkanBindlessMaterialCapability RefreshBindlessMaterialCapability(string? overrideReason = null)
    {
        EVulkanBindlessMaterialMode mode = VulkanFeatureProfile.ActiveBindlessMaterialMode;
        EVulkanBindlessMaterialCapabilityTier tier = EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingUnavailable;
        bool tableReady = _globalMaterialTextureDescriptorSet.Handle != 0;
        bool shaderReady = tableReady && VulkanFeatureProfile.EnableBindlessMaterialTable;
        bool drawPathReady = shaderReady && VulkanFeatureProfile.EnableGpuRenderDispatch;
        string reason = overrideReason ?? string.Empty;

        if (mode == EVulkanBindlessMaterialMode.Disabled)
            reason = "Vulkan bindless material mode is Disabled.";
        else if (!_supportsDescriptorIndexing ||
                 !_supportsRuntimeDescriptorArray ||
                 !_supportsDescriptorBindingPartiallyBound ||
                 !_supportsDescriptorBindingUpdateAfterBind)
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
            reason = "Vulkan GPU render dispatch remains disabled because IndirectDrawOp is not integrated with Vulkan render-pass/pipeline ownership.";

        _bindlessMaterialCapability = new VulkanBindlessMaterialCapability(
            mode,
            tier,
            _supportsDescriptorIndexing,
            _supportsRuntimeDescriptorArray,
            _supportsDescriptorBindingPartiallyBound,
            _supportsDescriptorBindingUpdateAfterBind,
            _globalMaterialTextureDescriptorCapacity,
            tableReady,
            shaderReady,
            drawPathReady,
            reason);

        return _bindlessMaterialCapability;
    }

    private void ValidateRequiredVulkanBindlessMaterialCapability()
    {
        VulkanBindlessMaterialCapability capability = RefreshBindlessMaterialCapability();
        if (capability.Mode != EVulkanBindlessMaterialMode.Required)
            return;

        if (capability.Tier >= EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady)
            return;

        throw new InvalidOperationException(
            $"Vulkan bindless material mode is Required but descriptor indexing prerequisites are unavailable. {capability.Reason}");
    }

    private string FormatBindlessDescriptorIndexingUnavailableReason()
        => $"Descriptor indexing prerequisites unavailable (descriptorIndexing={_supportsDescriptorIndexing}, runtimeArray={_supportsRuntimeDescriptorArray}, partiallyBound={_supportsDescriptorBindingPartiallyBound}, updateAfterBind={_supportsDescriptorBindingUpdateAfterBind}).";

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

    private void DestroyGlobalMaterialTextureDescriptorTable()
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
                Api!.DestroyDescriptorPool(device, _globalMaterialTextureDescriptorPool, null);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                _globalMaterialTextureDescriptorPool = default;
            }

            if (_globalMaterialTextureDescriptorSetLayout.Handle != 0)
            {
                ReleaseCachedDescriptorSetLayout(_globalMaterialTextureDescriptorSetLayout);
                _globalMaterialTextureDescriptorSetLayout = default;
            }

            _globalMaterialTextureDescriptorSet = default;
            _globalMaterialTextureDescriptorSetUsesUpdateAfterBind = false;
            _globalMaterialTextureDescriptorSetUsesVariableDescriptorCount = false;
            RefreshBindlessMaterialCapability("Global material texture descriptor table destroyed.");
        }
    }

    private struct MaterialTextureDescriptorSlot
    {
        public XRTexture? Texture;
        public DescriptorImageInfo ImageInfo;
        public uint Generation;
        public ulong LastUsedFrameId;
        public ulong RetireAfterFrameId;
        public bool Dirty;
        public bool PendingRetirement;
    }

    private sealed class ReferenceTextureComparer : IEqualityComparer<XRTexture>
    {
        public static readonly ReferenceTextureComparer Instance = new();

        public bool Equals(XRTexture? x, XRTexture? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(XRTexture obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
