using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Vectors;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    internal Pipeline ComputePipeline => _computePipeline;

    public Pipeline CreateComputePipeline(ref ComputePipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
    {
        if (!Link())
            throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");

        if (pipelineCache.Handle == 0)
            pipelineCache = BackendContext.Pipelines.ActivePipelineCache;

        PipelineShaderStageCreateInfo computeStage = GetShaderStages(EProgramStageMask.ComputeShaderBit).SingleOrDefault();
        if (computeStage.Module.Handle == 0)
            throw new InvalidOperationException("Compute pipeline creation requires a compute shader stage.");

        pipelineInfo.Stage = computeStage;
        pipelineInfo.Layout = _pipelineLayout;

        Result result;
        DescriptorHeapProgramLayout? descriptorHeapLayout = _descriptorHeapLayout;
        if (BackendContext.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
        {
            void* originalPipelinePNext = pipelineInfo.PNext;
            PipelineCreateFlags2CreateInfoNative flags2 = new()
            {
                SType = VulkanDescriptorHeapExt.PipelineCreateFlags2CreateInfoSType,
                PNext = originalPipelinePNext,
                Flags = unchecked((ulong)pipelineInfo.Flags) | VulkanDescriptorHeapExt.PipelineCreate2DescriptorHeapBit,
            };
            pipelineInfo.PNext = &flags2;

            if (descriptorHeapLayout is { Mappings.Length: > 0 })
            {
                fixed (DescriptorSetAndBindingMappingEXTNative* mappingPtr = descriptorHeapLayout.Mappings)
                {
                    void* originalStagePNext = pipelineInfo.Stage.PNext;
                    ShaderDescriptorSetAndBindingMappingInfoEXTNative mappingInfo = new()
                    {
                        SType = VulkanDescriptorHeapExt.ShaderDescriptorSetAndBindingMappingInfoSType,
                        PNext = originalStagePNext,
                        MappingCount = (uint)descriptorHeapLayout.Mappings.Length,
                        Mappings = mappingPtr,
                    };
                    pipelineInfo.Stage.PNext = &mappingInfo;
                    result = BackendContext.Pipelines.CreateComputePipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline mappedHeapPipeline);
                    pipelineInfo.Stage.PNext = originalStagePNext;
                    pipelineInfo.PNext = originalPipelinePNext;
                    if (result != Result.Success)
                        throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

                    BackendContext.ProgramServices.RegisterPipeline(mappedHeapPipeline, "VkRenderProgram.ComputeMappedHeap");
                    BackendContext.ProgramServices.NotifyPipelineCreated("compute");
                    return mappedHeapPipeline;
                }
            }

            result = BackendContext.Pipelines.CreateComputePipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline heapPipeline);
            pipelineInfo.PNext = originalPipelinePNext;
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

            BackendContext.ProgramServices.RegisterPipeline(heapPipeline, "VkRenderProgram.ComputeHeap");
            BackendContext.ProgramServices.NotifyPipelineCreated("compute");
            return heapPipeline;
        }

        result = BackendContext.Pipelines.CreateComputePipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline pipeline);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

        BackendContext.ProgramServices.RegisterPipeline(pipeline, "VkRenderProgram.Compute");
        BackendContext.ProgramServices.NotifyPipelineCreated("compute");
        return pipeline;
    }

    public ulong ComputeGraphicsPipelineFingerprint()
    {
        VulkanStableHash64 hash = new(schemaVersion: 2);
        hash.Add(VulkanPipelineManager.CommonPushConstantByteSize);

        for (int stageIndex = 0; stageIndex < VulkanProgramUtilities.StageOrder.Length; stageIndex++)
        {
            EProgramStageMask flag = VulkanProgramUtilities.StageOrder[stageIndex];
            if ((VulkanProgramUtilities.GraphicsStageMask & flag) == 0)
                continue;

            if (flag == EProgramStageMask.GeometryShaderBit && !BackendContext.Supports(EVulkanDeviceCapability.GeometryShader))
                continue;

            if (!_stageLookup.TryGetValue(flag, out VkShader? shader))
                continue;

            hash.Add((int)shader.StageFlags);
            hash.Add(shader.LastArtifact?.Identity ?? shader.CompileStatus.ArtifactIdentity ?? shader.StageDebugLabel);
        }

        hash.Add(_descriptorSetLayouts.Length);
        hash.Add((int)BackendContext.Descriptors.Heap.ActiveBackend);
        hash.Add(_descriptorHeapLayout?.PushByteCount ?? 0u);
        // Descriptor layout construction publishes this list in set/binding order.
        for (int i = 0; i < _programDescriptorBindings.Count; i++)
        {
            DescriptorBindingInfo binding = _programDescriptorBindings[i];
            hash.Add(binding.Set);
            hash.Add(binding.Binding);
            hash.Add((int)binding.DescriptorType);
            hash.Add(binding.Count);
            hash.Add((int)binding.StageFlags);
        }

        return hash.Value;
    }

    public ulong ComputeComputePipelineFingerprint()
    {
        VulkanStableHash64 hash = new(schemaVersion: 2);
        hash.Add(VulkanPipelineManager.CommonPushConstantByteSize);

        if (_stageLookup.TryGetValue(EProgramStageMask.ComputeShaderBit, out VkShader? shader))
        {
            hash.Add((int)shader.StageFlags);
            hash.Add(shader.LastArtifact?.Identity ?? shader.CompileStatus.ArtifactIdentity ?? shader.StageDebugLabel);
        }

        hash.Add(_descriptorSetLayouts.Length);
        hash.Add((int)BackendContext.Descriptors.Heap.ActiveBackend);
        hash.Add(_descriptorHeapLayout?.PushByteCount ?? 0u);
        // Descriptor layout construction publishes this list in set/binding order.
        for (int i = 0; i < _programDescriptorBindings.Count; i++)
        {
            DescriptorBindingInfo binding = _programDescriptorBindings[i];
            hash.Add(binding.Set);
            hash.Add(binding.Binding);
            hash.Add((int)binding.DescriptorType);
            hash.Add(binding.Count);
            hash.Add((int)binding.StageFlags);
        }

        return hash.Value;
    }

    private string ComputeProgramArtifactFingerprint()
        => $"VKPROG-{ComputeGraphicsPipelineFingerprint():X16}-{ComputeComputePipelineFingerprint():X16}";

    public Pipeline GetOrCreateComputePipeline(
        int passIndex = int.MinValue,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
    {
        if (_computePipeline.Handle != 0)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: true);
            return _computePipeline;
        }

        BackendContext.ProgramServices.RecordComputePipelineCacheMiss(
            passIndex,
            passMetadata,
            this,
            ComputeComputePipelineFingerprint());

        ComputePipelineCreateInfo pipelineInfo = new()
        {
            SType = StructureType.ComputePipelineCreateInfo
        };

        _computePipeline = CreateComputePipeline(ref pipelineInfo, BackendContext.Pipelines.ActivePipelineCache);
        return _computePipeline;
    }

    internal bool TryBuildAndBindComputeDescriptorSets(
        CommandBuffer commandBuffer,
        uint imageIndex,
        ComputeDispatchSnapshot snapshot,
        ulong reusableDescriptorBindingKey,
        out DescriptorPool descriptorPool,
        out DescriptorSet[] boundDescriptorSets,
        out IReadOnlyList<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> tempUniformBuffers)
    {
        descriptorPool = default;
        boundDescriptorSets = Array.Empty<DescriptorSet>();
        tempUniformBuffers = Array.Empty<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)>();

        if (_descriptorSetLayouts.Length == 0 || _programDescriptorBindings.Count == 0)
            return false;

        if (reusableDescriptorBindingKey != 0UL && BackendContext.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap)
        {
            ulong preparedSchemaFingerprint = ComputeComputeDescriptorSchemaFingerprint();
            if (!BackendContext.ProgramServices.TryGetPreparedComputeDescriptorSets(
                    imageIndex,
                    preparedSchemaFingerprint,
                    reusableDescriptorBindingKey,
                    out DescriptorSet[] preparedDescriptorSets))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.ComputeDispatch.PreparedDescriptorsMissing.{GetHashCode()}.{imageIndex}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Prepared compute descriptors are unavailable for '{0}'. image={1} schema=0x{2:X16} binding=0x{3:X16} candidates={4}.",
                    Data.Name ?? "UnnamedProgram",
                    imageIndex,
                    preparedSchemaFingerprint,
                    reusableDescriptorBindingKey,
                    BackendContext.ProgramServices.DescribePreparedComputeDescriptorBindings(
                        imageIndex,
                        preparedSchemaFingerprint));
                RecordComputeDescriptorFailure(
                    default,
                    "prepared descriptor sets were not published before recording",
                    skippedDispatch: true);
                return false;
            }

            BackendContext.ProgramServices.BindDescriptorSetsTracked(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                preparedDescriptorSets);
            boundDescriptorSets = preparedDescriptorSets;
            return true;
        }

        Dictionary<DescriptorType, uint> poolSizeCounts = new();
        foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
        {
            uint count = Math.Max(binding.Count, 1u);
            if (poolSizeCounts.TryGetValue(binding.DescriptorType, out uint existing))
                poolSizeCounts[binding.DescriptorType] = existing + count;
            else
                poolSizeCounts[binding.DescriptorType] = count;
        }

        if (poolSizeCounts.Count == 0)
            return false;

        DescriptorPoolSize[] poolSizes = poolSizeCounts
            .Select(p => new DescriptorPoolSize { Type = p.Key, DescriptorCount = p.Value })
            .ToArray();

        List<PendingDescriptorWrite> pendingWrites = [];
        List<DescriptorBufferInfo> bufferInfos = [];
        List<DescriptorImageInfo> imageInfos = [];
        List<BufferView> texelBufferViews = [];
        bool hasUnresolvedBinding = false;

        foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
        {
            if (binding.Set >= _descriptorSetLayouts.Length)
                continue;

            uint descriptorCount = Math.Max(binding.Count, 1u);
            switch (binding.DescriptorType)
            {
                case DescriptorType.UniformBuffer:
                case DescriptorType.StorageBuffer:
                    if (!TryResolveComputeBuffer(binding, imageIndex, snapshot, reusableDescriptorBindingKey, out DescriptorBufferInfo bufferInfo))
                    {
                        hasUnresolvedBinding = true;
                        WarnComputeOnce($"Skipping unresolved {binding.DescriptorType} binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}). Compute dispatch will be skipped.");
                        RecordComputeDescriptorFailure(binding, "buffer resolution failed", skippedDispatch: true);
                        continue;
                    }

                    int bufferStart = bufferInfos.Count;
                    for (int i = 0; i < descriptorCount; i++)
                        bufferInfos.Add(bufferInfo);

                    pendingWrites.Add(PendingDescriptorWrite.Buffer(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, bufferStart));
                    break;

                case DescriptorType.CombinedImageSampler:
                case DescriptorType.SampledImage:
                case DescriptorType.Sampler:
                case DescriptorType.StorageImage:
                    if (!TryResolveComputeImage(binding, snapshot, out DescriptorImageInfo imageInfo))
                    {
                        hasUnresolvedBinding = true;
                        WarnComputeOnce($"Skipping unresolved {binding.DescriptorType} image binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}). Compute dispatch will be skipped.");
                        RecordComputeDescriptorFailure(binding, "image resolution failed", skippedDispatch: true);
                        continue;
                    }

                    int imageStart = imageInfos.Count;
                    for (int i = 0; i < descriptorCount; i++)
                        imageInfos.Add(imageInfo);

                    pendingWrites.Add(PendingDescriptorWrite.Image(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, imageStart));
                    break;

                case DescriptorType.UniformTexelBuffer:
                case DescriptorType.StorageTexelBuffer:
                    if (!TryResolveComputeTexelBuffer(binding, snapshot, out BufferView texelView))
                    {
                        hasUnresolvedBinding = true;
                        WarnComputeOnce($"Skipping unresolved {binding.DescriptorType} texel binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}). Compute dispatch will be skipped.");
                        RecordComputeDescriptorFailure(binding, "texel buffer resolution failed", skippedDispatch: true);
                        continue;
                    }

                    int texelStart = texelBufferViews.Count;
                    for (int i = 0; i < descriptorCount; i++)
                        texelBufferViews.Add(texelView);

                    pendingWrites.Add(PendingDescriptorWrite.Texel(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, texelStart));
                    break;
            }
        }

        if (hasUnresolvedBinding)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                Data.Name,
                "descriptor-set",
                "<compute-required-binding>",
                0,
                0,
                skippedDraw: false,
                skippedDispatch: true,
                "compute descriptor build had unresolved required bindings");
            return false;
        }

        if (pendingWrites.Count == 0)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                Data.Name,
                "descriptor-set",
                "<none>",
                0,
                0,
                skippedDraw: false,
                skippedDispatch: true,
                "compute descriptor build produced no writes");
            return false;
        }

        PendingDescriptorWrite[] pendingWriteArray = pendingWrites.ToArray();
        DescriptorBufferInfo[] bufferArray = bufferInfos.ToArray();
        DescriptorImageInfo[] imageArray = imageInfos.ToArray();
        BufferView[] texelArray = texelBufferViews.ToArray();

        if (BackendContext.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
        {
            DescriptorHeapPushDataPayload payload = VulkanDescriptorManager.CreateHeapPushDataPayload(_descriptorHeapLayout);
            fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
            fixed (DescriptorImageInfo* imagePtr = imageArray)
            fixed (BufferView* texelPtr = texelArray)
            {
                for (int i = 0; i < pendingWriteArray.Length; i++)
                {
                    PendingDescriptorWrite pending = pendingWriteArray[i];
                    DescriptorBindingInfo binding = FindDescriptorBinding(pending.Set, pending.Binding, pending.DescriptorType);
                    bool wrote;
                    string heapReason;
                    switch (pending.Source)
                    {
                        case PendingDescriptorSource.Buffer:
                            wrote = BackendContext.DescriptorLifetime.TryWriteDescriptorHeapBinding(this, binding, payload, bufferPtr + pending.SourceStartIndex, null, null, pending.DescriptorCount, out heapReason);
                            break;
                        case PendingDescriptorSource.Image:
                            wrote = BackendContext.DescriptorLifetime.TryWriteDescriptorHeapBinding(this, binding, payload, null, imagePtr + pending.SourceStartIndex, null, pending.DescriptorCount, out heapReason);
                            break;
                        case PendingDescriptorSource.TexelBuffer:
                            wrote = BackendContext.DescriptorLifetime.TryWriteDescriptorHeapBinding(this, binding, payload, null, null, texelPtr + pending.SourceStartIndex, pending.DescriptorCount, out heapReason);
                            break;
                        default:
                            wrote = false;
                            heapReason = "unsupported compute descriptor source.";
                            break;
                    }

                    if (!wrote)
                    {
                        RecordComputeDescriptorFailure(binding, $"descriptor heap write failed: {heapReason}", skippedDispatch: true);
                        return false;
                    }
                }

                if (!BackendContext.ProgramServices.TryPushDescriptorHeapProgramData(commandBuffer, this, payload, out string pushReason))
                {
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                        Data.Name,
                        "descriptor-heap",
                        "<compute-push>",
                        0,
                        0,
                        skippedDraw: false,
                        skippedDispatch: true,
                        pushReason);
                    return false;
                }
            }

            return true;
        }

        ulong schemaFingerprint = ComputeComputeDescriptorSchemaFingerprint();
        ulong bindingFingerprint = ComputeComputeDescriptorBindingFingerprint(pendingWriteArray, bufferArray, imageArray, texelArray);
        ulong cacheBindingFingerprint = reusableDescriptorBindingKey == 0UL ? bindingFingerprint : reusableDescriptorBindingKey;
        DescriptorSetLayout[] layoutArray = _descriptorSetLayouts.ToArray();

        if (!BackendContext.ProgramServices.TryGetOrCreateComputeDescriptorSets(
            imageIndex,
            schemaFingerprint,
            cacheBindingFingerprint,
            layoutArray,
            poolSizes,
            _descriptorSetsRequireUpdateAfterBind,
            out DescriptorSet[] descriptorSets,
            out bool isNewAllocation))
        {
            WarnComputeOnce("Failed to acquire cached Vulkan compute descriptor sets.");
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                Data.Name,
                "descriptor-set",
                "<cached-compute>",
                0,
                0,
                skippedDraw: false,
                skippedDispatch: true,
                "failed to acquire cached compute descriptor sets");
            return false;
        }

        bool shouldUpdateDescriptorData = isNewAllocation || reusableDescriptorBindingKey != 0UL;
        if (shouldUpdateDescriptorData)
            UpdateComputeDescriptorSets(descriptorSets, pendingWriteArray, bufferArray, imageArray, texelArray);

        BackendContext.ProgramServices.BindDescriptorSetsTracked(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipelineLayout,
            0,
            descriptorSets);

        boundDescriptorSets = descriptorSets;

        return true;
    }

    private void UpdateComputeDescriptorSets(
        DescriptorSet[] descriptorSets,
        PendingDescriptorWrite[] pendingWrites,
        DescriptorBufferInfo[] bufferArray,
        DescriptorImageInfo[] imageArray,
        BufferView[] texelArray)
    {
        WriteDescriptorSet[] writeArray = new WriteDescriptorSet[pendingWrites.Length];
        for (int i = 0; i < pendingWrites.Length; i++)
        {
            PendingDescriptorWrite pending = pendingWrites[i];
            writeArray[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSets[pending.Set],
                DstBinding = pending.Binding,
                DescriptorCount = pending.DescriptorCount,
                DescriptorType = pending.DescriptorType
            };
        }

        fixed (WriteDescriptorSet* writePtr = writeArray)
        fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
        fixed (DescriptorImageInfo* imagePtr = imageArray)
        fixed (BufferView* texelPtr = texelArray)
        {
            for (int i = 0; i < pendingWrites.Length; i++)
            {
                PendingDescriptorWrite pending = pendingWrites[i];
                switch (pending.Source)
                {
                    case PendingDescriptorSource.Buffer:
                        writePtr[i].PBufferInfo = bufferPtr + pending.SourceStartIndex;
                        break;
                    case PendingDescriptorSource.Image:
                        writePtr[i].PImageInfo = imagePtr + pending.SourceStartIndex;
                        break;
                    case PendingDescriptorSource.TexelBuffer:
                        writePtr[i].PTexelBufferView = texelPtr + pending.SourceStartIndex;
                        break;
                }
            }

            if (!TryUpdateComputeDescriptorSetsWithTemplates(descriptorSets, writeArray))
                BackendContext.DescriptorLifetime.UpdateDescriptorSets((uint)writeArray.Length, writePtr);
            BackendContext.DescriptorLifetime.RecordTableGeneration();
        }
    }

    private static PushConstantRange CreateCommonPushConstantRange()
        => new()
        {
            StageFlags = VulkanPipelineManager.CommonPushConstantStages,
            Offset = 0,
            Size = VulkanPipelineManager.CommonPushConstantByteSize
        };

    private bool TryUpdateComputeDescriptorSetsWithTemplates(DescriptorSet[] descriptorSets, WriteDescriptorSet[] writeArray)
    {
        if (RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template)
            return false;

        if (_descriptorSetLayouts.Length < descriptorSets.Length)
            return false;

        for (int setIndex = 0; setIndex < descriptorSets.Length; setIndex++)
        {
            List<WriteDescriptorSet> setWrites = [];
            for (int i = 0; i < writeArray.Length; i++)
            {
                if (writeArray[i].DstSet.Handle == descriptorSets[setIndex].Handle)
                    setWrites.Add(writeArray[i]);
            }

            if (setWrites.Count == 0)
                continue;

            if (!BackendContext.DescriptorLifetime.TryUpdateDescriptorSetWithTemplate(
                descriptorSets[setIndex],
                _descriptorSetLayouts[setIndex],
                PipelineBindPoint.Compute,
                _pipelineLayout,
                (uint)setIndex,
                CollectionsMarshal.AsSpan(setWrites)))
            {
                return false;
            }
        }

        return true;
    }

    private ulong ComputeComputeDescriptorSchemaFingerprint()
    {
        ulong hash = 1469598103934665603UL;

        static void Mix(ref ulong value, ulong part)
        {
            value ^= part;
            value *= 1099511628211UL;
        }

        foreach (DescriptorBindingInfo binding in _programDescriptorBindings.OrderBy(b => b.Set).ThenBy(b => b.Binding))
        {
            Mix(ref hash, binding.Set);
            Mix(ref hash, binding.Binding);
            Mix(ref hash, (ulong)binding.DescriptorType);
            Mix(ref hash, binding.Count);
            Mix(ref hash, (ulong)binding.StageFlags);
        }

        foreach (DescriptorSetLayout layout in _descriptorSetLayouts)
            Mix(ref hash, layout.Handle);

        return hash;
    }

    private ulong ComputeComputeDescriptorBindingFingerprint(
        PendingDescriptorWrite[] writes,
        DescriptorBufferInfo[] buffers,
        DescriptorImageInfo[] images,
        BufferView[] texelViews)
    {
        ulong hash = 1469598103934665603UL;

        static void Mix(ref ulong value, ulong part)
        {
            value ^= part;
            value *= 1099511628211UL;
        }

        foreach (PendingDescriptorWrite write in writes)
        {
            Mix(ref hash, write.Set);
            Mix(ref hash, write.Binding);
            Mix(ref hash, (ulong)write.DescriptorType);
            Mix(ref hash, write.DescriptorCount);
            Mix(ref hash, (ulong)write.Source);

            for (uint i = 0; i < write.DescriptorCount; i++)
            {
                int index = write.SourceStartIndex + (int)i;
                switch (write.Source)
                {
                    case PendingDescriptorSource.Buffer:
                        {
                            DescriptorBufferInfo info = buffers[index];
                            Mix(ref hash, info.Buffer.Handle);
                            Mix(ref hash, BackendContext.GetResourceGeneration(ObjectType.Buffer, info.Buffer.Handle));
                            Mix(ref hash, info.Offset);
                            Mix(ref hash, info.Range);
                            break;
                        }
                    case PendingDescriptorSource.Image:
                        {
                            DescriptorImageInfo info = images[index];
                            Mix(ref hash, info.ImageView.Handle);
                            Mix(ref hash, BackendContext.GetResourceGeneration(ObjectType.ImageView, info.ImageView.Handle));
                            if (BackendContext.Images.TryGetBackingImage(info.ImageView, out Image backingImage))
                            {
                                Mix(ref hash, backingImage.Handle);
                                Mix(ref hash, BackendContext.GetResourceGeneration(ObjectType.Image, backingImage.Handle));
                            }
                            else
                            {
                                Mix(ref hash, 0UL);
                                Mix(ref hash, 0UL);
                            }
                            Mix(ref hash, info.Sampler.Handle);
                            Mix(ref hash, BackendContext.GetResourceGeneration(ObjectType.Sampler, info.Sampler.Handle));
                            Mix(ref hash, (ulong)info.ImageLayout);
                            break;
                        }
                    case PendingDescriptorSource.TexelBuffer:
                        {
                            BufferView view = texelViews[index];
                            Mix(ref hash, view.Handle);
                            Mix(ref hash, BackendContext.GetResourceGeneration(ObjectType.BufferView, view.Handle));
                            if (BackendContext.TryGetBufferViewBackingBuffer(view, out Silk.NET.Vulkan.Buffer backingBuffer))
                            {
                                Mix(ref hash, backingBuffer.Handle);
                                Mix(ref hash, BackendContext.GetResourceGeneration(ObjectType.Buffer, backingBuffer.Handle));
                            }
                            else
                            {
                                Mix(ref hash, 0UL);
                                Mix(ref hash, 0UL);
                            }
                            break;
                        }
                }
            }
        }

        return hash;
    }

    private enum PendingDescriptorSource : byte
    {
        Buffer,
        Image,
        TexelBuffer
    }

    private readonly record struct PendingDescriptorWrite(
        uint Set,
        uint Binding,
        DescriptorType DescriptorType,
        uint DescriptorCount,
        PendingDescriptorSource Source,
        int SourceStartIndex)
    {
        public static PendingDescriptorWrite Buffer(uint set, uint binding, DescriptorType descriptorType, uint descriptorCount, int sourceStartIndex)
            => new(set, binding, descriptorType, descriptorCount, PendingDescriptorSource.Buffer, sourceStartIndex);

        public static PendingDescriptorWrite Image(uint set, uint binding, DescriptorType descriptorType, uint descriptorCount, int sourceStartIndex)
            => new(set, binding, descriptorType, descriptorCount, PendingDescriptorSource.Image, sourceStartIndex);

        public static PendingDescriptorWrite Texel(uint set, uint binding, DescriptorType descriptorType, uint descriptorCount, int sourceStartIndex)
            => new(set, binding, descriptorType, descriptorCount, PendingDescriptorSource.TexelBuffer, sourceStartIndex);
    }

    private DescriptorBindingInfo FindDescriptorBinding(uint set, uint binding, DescriptorType descriptorType)
    {
        for (int i = 0; i < _programDescriptorBindings.Count; i++)
        {
            DescriptorBindingInfo candidate = _programDescriptorBindings[i];
            if (candidate.Set == set && candidate.Binding == binding)
                return candidate;
        }

        return new DescriptorBindingInfo(set, binding, descriptorType, ShaderStageFlags.ComputeBit, 1u, string.Empty);
    }

    private static string GetDescriptorBindingClass(DescriptorType descriptorType)
        => descriptorType switch
        {
            DescriptorType.StorageImage => "storage-image",
            DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => "uniform-buffer",
            DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => "storage-buffer",
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer => "texel-buffer",
            _ => "sampled-image",
        };

    private void RecordComputeDescriptorFallback(DescriptorBindingInfo binding, int count = 1)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorFallback(
            Data.Name,
            GetDescriptorBindingClass(binding.DescriptorType),
            binding.Name,
            binding.Set,
            binding.Binding,
            count);

    private void RecordComputeDescriptorFailure(DescriptorBindingInfo binding, string reason, bool skippedDispatch)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
            Data.Name,
            GetDescriptorBindingClass(binding.DescriptorType),
            binding.Name,
            binding.Set,
            binding.Binding,
            skippedDraw: false,
            skippedDispatch,
            reason);

    private bool TryResolveComputeBuffer(
        DescriptorBindingInfo binding,
        uint imageIndex,
        ComputeDispatchSnapshot snapshot,
        ulong dispatchKey,
        out DescriptorBufferInfo bufferInfo)
    {
        bufferInfo = default;

        if (snapshot.Buffers.TryGetValue(binding.Binding, out VulkanComputeBufferBinding boundBuffer))
            return TryCreateDescriptorBufferInfo(binding, boundBuffer, out bufferInfo);

        if (binding.DescriptorType == DescriptorType.UniformBuffer &&
            TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
        {
            if (TryGetOrUpdateComputeAutoUniformBuffer(imageIndex, binding, snapshot, block, dispatchKey, out bufferInfo))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(binding.Name))
        {
            if (snapshot.BuffersByName.TryGetValue(binding.Name, out VulkanComputeBufferBinding namedBuffer) &&
                TryCreateDescriptorBufferInfo(binding, namedBuffer, out bufferInfo))
            {
                return true;
            }
        }

        if (binding.Requirement == EVulkanDescriptorBindingRequirement.Optional &&
            binding.DescriptorType == DescriptorType.UniformBuffer &&
            TryGetOrUpdateComputeFallbackUniformBuffer(imageIndex, binding, dispatchKey, out bufferInfo))
        {
            RecordComputeDescriptorFallback(binding);
            return true;
        }

        return false;
    }

    private bool TryGetOrUpdateComputeFallbackUniformBuffer(
        uint imageIndex,
        DescriptorBindingInfo binding,
        ulong dispatchKey,
        out DescriptorBufferInfo bufferInfo)
    {
        bufferInfo = default;

        const uint fallbackSize = 4096u;
        ComputeUniformBufferKey key = new(
            EComputeUniformBufferKind.Fallback,
            imageIndex,
            binding.Set,
            binding.Binding,
            binding.Name ?? string.Empty,
            dispatchKey);

        if (!TryGetOrCreateComputeUniformBuffer(key, fallbackSize, out ComputeUniformBuffer resource, out bool created))
            return false;

        if (created && !ClearComputeUniformBuffer(resource, fallbackSize))
        {
            _computeUniformBuffers.Remove(key);
            ReleaseComputeUniformBuffer(resource);
            return false;
        }

        bufferInfo = new DescriptorBufferInfo
        {
            Buffer = resource.Buffer,
            Offset = 0,
            Range = fallbackSize
        };

        WarnComputeOnce($"Using zero-filled cached fallback uniform buffer for unresolved binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
        return true;
    }

    private bool TryGetOrUpdateComputeAutoUniformBuffer(
        uint imageIndex,
        DescriptorBindingInfo binding,
        ComputeDispatchSnapshot snapshot,
        AutoUniformBlockInfo block,
        ulong dispatchKey,
        out DescriptorBufferInfo bufferInfo)
    {
        bufferInfo = default;

        uint size = Math.Max(block.Size, 1u);
        ComputeUniformBufferKey key = new(
            EComputeUniformBufferKind.Auto,
            imageIndex,
            binding.Set,
            binding.Binding,
            block.InstanceName,
            dispatchKey);

        if (!TryGetOrCreateComputeUniformBuffer(key, size, out ComputeUniformBuffer resource, out _))
            return false;

        if (!TryWriteComputeAutoUniformBuffer(resource, size, snapshot, block))
            return false;

        bufferInfo = new DescriptorBufferInfo
        {
            Buffer = resource.Buffer,
            Offset = 0,
            Range = size
        };

        return true;
    }

    /// <summary>
    /// Materializes persistent compute uniform buffers and reusable descriptor sets
    /// before a command buffer enters its recording scope.
    /// </summary>
    internal bool TryPrepareComputeDispatchResources(
        uint imageIndex,
        ComputeDispatchSnapshot snapshot,
        ulong reusableDescriptorBindingKey)
    {
        if (_descriptorSetLayouts.Length == 0 || _programDescriptorBindings.Count == 0)
            return true;

        foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
        {
            if (binding.Set >= _descriptorSetLayouts.Length ||
                binding.DescriptorType != DescriptorType.UniformBuffer)
            {
                continue;
            }

            bool hasSnapshotBuffer = snapshot.Buffers.ContainsKey(binding.Binding);
            if (!hasSnapshotBuffer)
                hasSnapshotBuffer = SnapshotContainsNamedBuffer(snapshot, binding.Name);
            if (hasSnapshotBuffer)
                continue;

            if (TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
            {
                if (!TryGetOrUpdateComputeAutoUniformBuffer(
                    imageIndex,
                    binding,
                    snapshot,
                    block,
                    reusableDescriptorBindingKey,
                    out _))
                {
                    return false;
                }

                continue;
            }

            if (binding.Requirement == EVulkanDescriptorBindingRequirement.Required)
            {
                RecordComputeDescriptorFailure(binding, "required uniform buffer is unresolved during pre-native preparation", skippedDispatch: true);
                return false;
            }

            if (!TryGetOrUpdateComputeFallbackUniformBuffer(
                    imageIndex,
                    binding,
                    reusableDescriptorBindingKey,
                    out _))
            {
                RecordComputeDescriptorFailure(binding, "optional fallback uniform buffer preparation failed", skippedDispatch: true);
                return false;
            }
        }

        if (reusableDescriptorBindingKey == 0UL || BackendContext.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
            return true;

        return TryRefreshReusableComputeDescriptorSets(
            imageIndex,
            snapshot,
            reusableDescriptorBindingKey);
    }
    internal bool TryRefreshReusableComputeDispatchFrameData(uint imageIndex, ComputeDispatchSnapshot snapshot, ulong reusableDescriptorBindingKey)
    {
        if (_descriptorSetLayouts.Length == 0 || _programDescriptorBindings.Count == 0)
            return true;

        foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
        {
            if (binding.Set >= _descriptorSetLayouts.Length ||
                binding.DescriptorType != DescriptorType.UniformBuffer)
            {
                continue;
            }

            bool hasSnapshotBuffer = snapshot.Buffers.ContainsKey(binding.Binding);
            if (!hasSnapshotBuffer)
                hasSnapshotBuffer = SnapshotContainsNamedBuffer(snapshot, binding.Name);
            if (hasSnapshotBuffer)
                continue;

            if (TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
            {
                if (!TryUpdateExistingComputeAutoUniformBuffer(imageIndex, binding, snapshot, block, reusableDescriptorBindingKey))
                    return false;
                continue;
            }

            if (binding.Requirement == EVulkanDescriptorBindingRequirement.Required)
            {
                RecordComputeDescriptorFailure(binding, "required uniform buffer is unresolved during frame-data refresh", skippedDispatch: true);
                return false;
            }

            if (!HasExistingComputeFallbackUniformBuffer(imageIndex, binding, reusableDescriptorBindingKey))
                return false;
        }

        return TryRefreshReusableComputeDescriptorSets(imageIndex, snapshot, reusableDescriptorBindingKey);
    }

    private bool TryRefreshReusableComputeDescriptorSets(uint imageIndex, ComputeDispatchSnapshot snapshot, ulong reusableDescriptorBindingKey)
    {
        if (reusableDescriptorBindingKey == 0UL)
            return true;

        (uint ImageIndex, ulong BindingKey) refreshKey = (imageIndex, reusableDescriptorBindingKey);
        snapshot.ResolvePublishedResourceSignatures(
            BackendContext.ProgramServices.ResolveDescriptorViewFamilyIdentity(),
            out _,
            out ulong resourceSignature);
        ulong schemaFingerprint = ComputeComputeDescriptorSchemaFingerprint();
        if (snapshot.HasPublishedBindingLayoutSignatures &&
            _reusableComputeDescriptorResourceSignatures.TryGetValue(
                refreshKey,
                out ulong publishedResourceSignature) &&
            publishedResourceSignature == resourceSignature &&
            BackendContext.ProgramServices.TryGetPreparedComputeDescriptorSets(
                imageIndex,
                schemaFingerprint,
                reusableDescriptorBindingKey,
                out _))
        {
            return true;
        }

        Dictionary<DescriptorType, uint> poolSizeCounts = new();
        foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
        {
            uint count = Math.Max(binding.Count, 1u);
            if (poolSizeCounts.TryGetValue(binding.DescriptorType, out uint existing))
                poolSizeCounts[binding.DescriptorType] = existing + count;
            else
                poolSizeCounts[binding.DescriptorType] = count;
        }

        if (poolSizeCounts.Count == 0)
            return true;

        DescriptorPoolSize[] poolSizes = poolSizeCounts
            .Select(p => new DescriptorPoolSize { Type = p.Key, DescriptorCount = p.Value })
            .ToArray();

        List<PendingDescriptorWrite> pendingWrites = [];
        List<DescriptorBufferInfo> bufferInfos = [];
        List<DescriptorImageInfo> imageInfos = [];
        List<BufferView> texelBufferViews = [];
        bool hasUnresolvedBinding = false;

        foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
        {
            if (binding.Set >= _descriptorSetLayouts.Length)
                continue;

            uint descriptorCount = Math.Max(binding.Count, 1u);
            switch (binding.DescriptorType)
            {
                case DescriptorType.UniformBuffer:
                case DescriptorType.StorageBuffer:
                    if (!TryResolveComputeBuffer(binding, imageIndex, snapshot, reusableDescriptorBindingKey, out DescriptorBufferInfo bufferInfo))
                    {
                        hasUnresolvedBinding = true;
                        RecordComputeDescriptorFailure(binding, "buffer refresh failed", skippedDispatch: true);
                        continue;
                    }

                    int bufferStart = bufferInfos.Count;
                    for (int i = 0; i < descriptorCount; i++)
                        bufferInfos.Add(bufferInfo);

                    pendingWrites.Add(PendingDescriptorWrite.Buffer(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, bufferStart));
                    break;

                case DescriptorType.CombinedImageSampler:
                case DescriptorType.SampledImage:
                case DescriptorType.Sampler:
                case DescriptorType.StorageImage:
                    if (!TryResolveComputeImage(binding, snapshot, out DescriptorImageInfo imageInfo))
                    {
                        hasUnresolvedBinding = true;
                        RecordComputeDescriptorFailure(binding, "image refresh failed", skippedDispatch: true);
                        continue;
                    }

                    int imageStart = imageInfos.Count;
                    for (int i = 0; i < descriptorCount; i++)
                        imageInfos.Add(imageInfo);

                    pendingWrites.Add(PendingDescriptorWrite.Image(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, imageStart));
                    break;

                case DescriptorType.UniformTexelBuffer:
                case DescriptorType.StorageTexelBuffer:
                    if (!TryResolveComputeTexelBuffer(binding, snapshot, out BufferView texelView))
                    {
                        hasUnresolvedBinding = true;
                        RecordComputeDescriptorFailure(binding, "texel refresh failed", skippedDispatch: true);
                        continue;
                    }

                    int texelStart = texelBufferViews.Count;
                    for (int i = 0; i < descriptorCount; i++)
                        texelBufferViews.Add(texelView);

                    pendingWrites.Add(PendingDescriptorWrite.Texel(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, texelStart));
                    break;
            }
        }

        if (hasUnresolvedBinding || pendingWrites.Count == 0)
            return false;

        DescriptorSetLayout[] layoutArray = _descriptorSetLayouts.ToArray();
        if (!BackendContext.ProgramServices.TryGetOrCreateComputeDescriptorSets(
            imageIndex,
            schemaFingerprint,
            reusableDescriptorBindingKey,
            layoutArray,
            poolSizes,
            _descriptorSetsRequireUpdateAfterBind,
            out DescriptorSet[] descriptorSets,
            out _))
        {
            return false;
        }

        UpdateComputeDescriptorSets(
            descriptorSets,
            pendingWrites.ToArray(),
            bufferInfos.ToArray(),
            imageInfos.ToArray(),
            texelBufferViews.ToArray());
        if (snapshot.HasPublishedBindingLayoutSignatures)
            _reusableComputeDescriptorResourceSignatures[refreshKey] =
                resourceSignature;
        return true;
    }

    private static bool SnapshotContainsNamedBuffer(ComputeDispatchSnapshot snapshot, string? bindingName)
        => !string.IsNullOrWhiteSpace(bindingName) && snapshot.BuffersByName.ContainsKey(bindingName);

    private bool TryUpdateExistingComputeAutoUniformBuffer(
        uint imageIndex,
        DescriptorBindingInfo binding,
        ComputeDispatchSnapshot snapshot,
        AutoUniformBlockInfo block,
        ulong dispatchKey)
    {
        uint size = Math.Max(block.Size, 1u);
        ComputeUniformBufferKey key = new(
            EComputeUniformBufferKind.Auto,
            imageIndex,
            binding.Set,
            binding.Binding,
            block.InstanceName,
            dispatchKey);

        if (!_computeUniformBuffers.TryGetValue(key, out ComputeUniformBuffer resource) ||
            resource.Buffer.Handle == 0 ||
            resource.Size < size)
        {
            return false;
        }

        return TryWriteComputeAutoUniformBuffer(resource, size, snapshot, block);
    }

    private bool HasExistingComputeFallbackUniformBuffer(uint imageIndex, DescriptorBindingInfo binding, ulong dispatchKey)
    {
        const uint fallbackSize = 4096u;
        ComputeUniformBufferKey key = new(
            EComputeUniformBufferKind.Fallback,
            imageIndex,
            binding.Set,
            binding.Binding,
            binding.Name ?? string.Empty,
            dispatchKey);

        return _computeUniformBuffers.TryGetValue(key, out ComputeUniformBuffer resource) &&
            resource.Buffer.Handle != 0 &&
            resource.Size >= fallbackSize;
    }

    private bool TryWriteComputeAutoUniformBuffer(
        ComputeUniformBuffer resource,
        uint size,
        ComputeDispatchSnapshot snapshot,
        AutoUniformBlockInfo block)
    {
        if (resource.Mapped == null || resource.Size < size)
            return false;

        Span<byte> data = new(resource.Mapped, (int)size);
        data.Clear();

        IReadOnlyList<AutoUniformMember> members = block.Members;
        for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
            TryWriteAutoUniformMember(data, members[memberIndex], snapshot);

        return true;
    }

    private bool TryGetOrCreateComputeUniformBuffer(
        ComputeUniformBufferKey key,
        uint size,
        out ComputeUniformBuffer resource,
        out bool created)
    {
        created = false;
        size = Math.Max(size, 1u);

        if (_computeUniformBuffers.TryGetValue(key, out resource) &&
            resource.Buffer.Handle != 0 &&
            resource.Size >= size)
        {
            return true;
        }

        if (resource.Buffer.Handle != 0 || resource.Memory.Handle != 0)
            ReleaseComputeUniformBuffer(resource);

        (Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) = BackendContext.Buffers.Create(BackendContext,
            size,
            BackendContext.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap
                ? BufferUsageFlags.UniformBufferBit | BufferUsageFlags.ShaderDeviceAddressBit
                : BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            null,
            BackendContext.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap);

        if (buffer.Handle == 0 || memory.Handle == 0)
        {
            resource = default;
            return false;
        }

        if (!BackendContext.Buffers.TryMap(BackendContext,buffer, memory, 0, size, out void* mapped))
        {
            BackendContext.Buffers.Retire(buffer, memory, "VkRenderProgram.ComputeUniformBuffer");
            resource = default;
            return false;
        }

        resource = new ComputeUniformBuffer(buffer, memory, size, mapped);
        _computeUniformBuffers[key] = resource;
        created = true;
        return true;
    }

    private bool ClearComputeUniformBuffer(ComputeUniformBuffer resource, uint size)
    {
        if (resource.Mapped == null || resource.Size < size)
            return false;

        Span<byte> data = new(resource.Mapped, (int)size);
        data.Clear();
        return true;
    }

    private bool TryCreateDescriptorBufferInfo(
        DescriptorBindingInfo binding,
        XRDataBuffer dataBuffer,
        out DescriptorBufferInfo bufferInfo)
    {
        bufferInfo = default;
        bool allowSynchronousBufferUpload = BackendContext.AllowSynchronousResourceUploads;
        if (BackendContext.GetOrCreateAPIRenderObject(dataBuffer, generateNow: allowSynchronousBufferUpload) is not VkDataBuffer vkBuffer)
            return false;

        if (!vkBuffer.TryEnsureReadyForRendering(allowSynchronousBufferUpload))
            return false;

        if (vkBuffer.BufferHandle is not { } handle || handle.Handle == 0)
            return false;

        ulong requestedRange = Math.Max((ulong)dataBuffer.Length, 1UL);
        if (vkBuffer.AllocatedByteSize < requestedRange)
        {
            if (!allowSynchronousBufferUpload)
                return false;

            vkBuffer.PushData();
            handle = vkBuffer.BufferHandle ?? default;
        }

        if (handle.Handle == 0 || vkBuffer.AllocatedByteSize < requestedRange)
            return false;

        if (!vkBuffer.SupportsDescriptorType(binding.DescriptorType))
        {
            WarnComputeOnce(
                $"Skipping Vulkan compute binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}) because buffer '{dataBuffer.AttributeName}' was created for {dataBuffer.Target}/{vkBuffer.LastUsageFlags}, not {binding.DescriptorType}. Compute dispatch will be skipped.");
            return false;
        }

        bufferInfo = new DescriptorBufferInfo
        {
            Buffer = handle,
            Offset = 0,
            Range = requestedRange
        };
        return true;
    }

    private bool TryCreateDescriptorBufferInfo(
        DescriptorBindingInfo binding,
        VulkanComputeBufferBinding snapshot,
        out DescriptorBufferInfo bufferInfo)
    {
        bufferInfo = default;
        if (snapshot.Buffer.Handle == 0 || snapshot.Range == 0)
            return false;

        if (!VkDataBuffer.SupportsDescriptorType(binding.DescriptorType, snapshot.UsageFlags))
            return false;

        bufferInfo = new DescriptorBufferInfo
        {
            Buffer = snapshot.Buffer,
            Offset = 0,
            Range = snapshot.Range
        };
        return true;
    }

    private bool TryResolveComputeImage(DescriptorBindingInfo binding, ComputeDispatchSnapshot snapshot, out DescriptorImageInfo imageInfo)
    {
        imageInfo = default;

        if (binding.DescriptorType == DescriptorType.StorageImage)
        {
            if (!snapshot.Images.TryGetValue(binding.Binding, out ProgramImageBinding imageBinding))
                return false;

            if (!TryResolveTextureDescriptor(binding, imageBinding.Texture, includeSampler: false, requiresSampledUsage: false, requiresStorageUsage: true, ImageLayout.General, out imageInfo))
                return false;

            return true;
        }

        if (!snapshot.Samplers.TryGetValue(binding.Binding, out XRTexture? texture))
        {
            if (binding.Requirement == EVulkanDescriptorBindingRequirement.Required)
                return false;

            // Fallback for shaders that only bind a single sampler but use non-zero binding in source.
            texture = snapshot.Samplers.Count == 1 ? snapshot.Samplers.Values.First() : null;
            if (texture is null)
                return false;

            WarnComputeOnce($"Image binding {binding.Binding} ('{binding.Name}') not found in snapshot; using only available sampler '{texture.Name ?? "<unnamed>"}' as fallback.");
            RecordComputeDescriptorFallback(binding);
        }

        bool includeSampler = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler;
        bool requiresSampledUsage = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler or DescriptorType.SampledImage;
        return TryResolveTextureDescriptor(binding, texture, includeSampler, requiresSampledUsage, requiresStorageUsage: false, ImageLayout.ShaderReadOnlyOptimal, out imageInfo);
    }

    private bool TryResolveComputeTexelBuffer(DescriptorBindingInfo binding, ComputeDispatchSnapshot snapshot, out BufferView texelView)
    {
        texelView = default;

        if (!snapshot.Samplers.TryGetValue(binding.Binding, out XRTexture? texture))
        {
            if (binding.Requirement == EVulkanDescriptorBindingRequirement.Required)
                return false;

            texture = snapshot.Samplers.Count == 1 ? snapshot.Samplers.Values.First() : null;
            if (texture is null)
                return false;

            WarnComputeOnce($"Texel binding {binding.Binding} ('{binding.Name}') not found in snapshot; using only available sampler '{texture.Name ?? "<unnamed>"}' as fallback.");
            RecordComputeDescriptorFallback(binding);
        }

        return TryResolveTexelBufferDescriptor(texture, out texelView);
    }

    private bool TryResolveTextureDescriptor(DescriptorBindingInfo binding, XRTexture texture, bool includeSampler, bool requiresSampledUsage, bool requiresStorageUsage, ImageLayout layout, out DescriptorImageInfo imageInfo)
    {
        imageInfo = default;
        if (texture is null)
            return false;

        bool allowSynchronousTextureUpload = BackendContext.AllowSynchronousResourceUploads;
        if (BackendContext.GetOrCreateAPIRenderObject(texture, generateNow: allowSynchronousTextureUpload) is not IVkImageDescriptorSource source)
            return false;

        if (!source.TryEnsureDescriptorReadyForUse($"compute descriptor '{binding.Name}'", allowSynchronousTextureUpload))
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Descriptor.TextureNotReady.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping descriptor bind for texture '{0}' because its Vulkan descriptor source is not ready.",
                texture.Name ?? texture.GetDescribingName());
            return false;
        }

        if (requiresSampledUsage && (source.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Descriptor.NoSampledUsage.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping sampled descriptor bind for texture '{0}' (usage={1}) because VK_IMAGE_USAGE_SAMPLED_BIT is not set.",
                texture.Name ?? texture.GetDescribingName(),
                source.DescriptorUsage);
            return false;
        }

        if (requiresStorageUsage && (source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Descriptor.NoStorageUsage.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping storage descriptor bind for texture '{0}' (usage={1}) because VK_IMAGE_USAGE_STORAGE_BIT is not set.",
                texture.Name ?? texture.GetDescribingName(),
                source.DescriptorUsage);
            return false;
        }

        ImageView descriptorView = source.DescriptorView;
        ImageAspectFlags descriptorAspect = source.DescriptorAspect;
        if (IsCombinedDepthStencilFormat(source.DescriptorFormat) &&
            (descriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
        {
            // Descriptor bindings for depth-stencil images must target a single aspect view.
            // Request a depth-only view instead of skipping the bind entirely.
            ImageView depthOnlyView = source.GetDepthOnlyDescriptorView();
            if (depthOnlyView.Handle != 0)
            {
                descriptorView = depthOnlyView;
                descriptorAspect = ImageAspectFlags.DepthBit;
            }
            else
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Descriptor.DepthStencilCombinedAspect.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping descriptor bind for texture '{0}' because no depth-only view is available.",
                    texture.Name ?? texture.GetDescribingName());
                return false;
            }
        }

        if (!BackendContext.Images.IsLiveBackedByLiveImage(descriptorView))
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Descriptor.RetiredImageView.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping descriptor bind for texture '{0}' because its Vulkan image view has been retired.",
                texture.Name ?? texture.GetDescribingName());
            return false;
        }

        if (!TryResolveComputeDescriptorSampler(includeSampler, binding, source, out Sampler sampler))
            return false;

        ImageLayout descriptorLayout = VulkanProgramUtilities.ResolveDescriptorImageLayout(
            source,
            requiresStorageUsage ? DescriptorType.StorageImage : DescriptorType.SampledImage);

        imageInfo = new DescriptorImageInfo
        {
            ImageLayout = descriptorLayout,
            ImageView = descriptorView,
            Sampler = sampler
        };
        return imageInfo.ImageView.Handle != 0;
    }

    private bool TryResolveComputeDescriptorSampler(bool includeSampler, DescriptorBindingInfo binding, IVkImageDescriptorSource source, out Sampler sampler)
    {
        sampler = default;
        if (!includeSampler)
            return true;

        sampler = source.DescriptorSampler;
        if (sampler.Handle != 0 && BackendContext.Descriptors.IsLiveSampler(sampler))
            return true;

        if (sampler.Handle != 0)
        {
            if (binding.Requirement == EVulkanDescriptorBindingRequirement.Required)
                return false;

            WarnComputeOnce($"Compute texture for binding '{binding.Name}' references a retired Vulkan sampler. Using placeholder sampler.");
            RecordComputeDescriptorFallback(binding);
        }

        if (binding.Requirement == EVulkanDescriptorBindingRequirement.Required)
            return false;

        sampler = BackendContext.FallbackTexture.GetSampler();
        if (sampler.Handle != 0 && BackendContext.Descriptors.IsLiveSampler(sampler))
        {
            WarnComputeOnce($"Compute texture for binding '{binding.Name}' has no Vulkan sampler. Using placeholder sampler.");
            RecordComputeDescriptorFallback(binding);
            return true;
        }

        WarnComputeOnce($"Compute texture for binding '{binding.Name}' has no Vulkan sampler and placeholder sampler is unavailable.");
        RecordComputeDescriptorFailure(binding, "texture sampler unavailable", skippedDispatch: false);
        return false;
    }

    private static bool IsCombinedDepthStencilFormat(Format format)
        => format is Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.D16UnormS8Uint;

    private bool TryResolveTexelBufferDescriptor(XRTexture texture, out BufferView texelView)
    {
        texelView = default;
        if (texture is null)
            return false;

        if (BackendContext.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
            return false;

        texelView = source.DescriptorBufferView;
        return texelView.Handle != 0;
    }

}
