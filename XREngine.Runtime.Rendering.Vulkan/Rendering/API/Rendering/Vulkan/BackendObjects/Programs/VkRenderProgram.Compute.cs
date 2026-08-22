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
    private readonly VulkanComputeDescriptorScratchBuilder _computeDescriptorScratch = new();

    /// <summary>Allocation-free work counters for this program's compute descriptor publication scratch.</summary>
    internal VulkanComputeDescriptorScratchBuilder.Telemetry ComputeDescriptorPublicationTelemetry
        => _computeDescriptorScratch.GetTelemetry();
    internal Pipeline ComputePipeline => _computePipeline;

    public Pipeline CreateComputePipeline(ref ComputePipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
    {
        if (!Link())
            throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");

        if (pipelineCache.Handle == 0)
            pipelineCache = BackendContext.Resources.PipelineManager.ActivePipelineCache;

        PipelineShaderStageCreateInfo computeStage = GetShaderStages(EProgramStageMask.ComputeShaderBit).SingleOrDefault();
        if (computeStage.Module.Handle == 0)
            throw new InvalidOperationException("Compute pipeline creation requires a compute shader stage.");

        pipelineInfo.Stage = computeStage;
        pipelineInfo.Layout = _pipelineLayout;

        Result result;
        DescriptorHeapProgramLayout? descriptorHeapLayout = _descriptorHeapLayout;
        if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
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
                    result = BackendContext.Resources.PipelineManager.CreateComputePipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline mappedHeapPipeline);
                    pipelineInfo.Stage.PNext = originalStagePNext;
                    pipelineInfo.PNext = originalPipelinePNext;
                    if (result != Result.Success)
                        throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

                    ProgramCreationPort.RegisterPipeline(mappedHeapPipeline, "VkRenderProgram.ComputeMappedHeap");
                    ProgramCreationPort.NotifyPipelineCreated("compute");
                    return mappedHeapPipeline;
                }
            }

            result = BackendContext.Resources.PipelineManager.CreateComputePipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline heapPipeline);
            pipelineInfo.PNext = originalPipelinePNext;
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

            ProgramCreationPort.RegisterPipeline(heapPipeline, "VkRenderProgram.ComputeHeap");
            ProgramCreationPort.NotifyPipelineCreated("compute");
            return heapPipeline;
        }

        result = BackendContext.Resources.PipelineManager.CreateComputePipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline pipeline);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

        ProgramCreationPort.RegisterPipeline(pipeline, "VkRenderProgram.Compute");
        ProgramCreationPort.NotifyPipelineCreated("compute");
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
        hash.Add((int)BackendContext.Resources.Descriptors.Heap.ActiveBackend);
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
        hash.Add((int)BackendContext.Resources.Descriptors.Heap.ActiveBackend);
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

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: false);

        ComputePipelineCreateInfo pipelineInfo = new()
        {
            SType = StructureType.ComputePipelineCreateInfo
        };

        _computePipeline = CreateComputePipeline(ref pipelineInfo, BackendContext.Resources.PipelineManager.ActivePipelineCache);
        return _computePipeline;
    }

    internal bool TryBuildAndBindComputeDescriptorSets(
        in VulkanProgramRecordingRequest recording,
        uint imageIndex,
        ComputeDispatchSnapshot snapshot,
        ulong reusableDescriptorBindingKey,
        PipelineBindPoint bindPoint,
        out DescriptorPool descriptorPool,
        out DescriptorSet[] boundDescriptorSets,
        out IReadOnlyList<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> tempUniformBuffers,
        bool excludeGlobalTextureArray = false)
    {
        descriptorPool = default;
        boundDescriptorSets = Array.Empty<DescriptorSet>();
        tempUniformBuffers = Array.Empty<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)>();

        if (excludeGlobalTextureArray && !_canBindGlobalTextureArraySeparately)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                Data.Name,
                "descriptor-set",
                VulkanBindlessMaterialDescriptors.TextureArrayBindingName,
                VulkanBindlessMaterialDescriptors.TextureArraySet,
                VulkanBindlessMaterialDescriptors.TextureArrayBinding,
                skippedDraw: bindPoint == PipelineBindPoint.Graphics,
                skippedDispatch: bindPoint == PipelineBindPoint.Compute,
                "global material set cannot be separated because it is missing, shared with another binding, or followed by a non-empty set");
            return false;
        }

        DescriptorSetLayout[] descriptorLayouts =
            excludeGlobalTextureArray
                ? _descriptorSetLayoutsBeforeGlobalMaterial
                : _descriptorSetLayouts;
        uint descriptorSetLimit = checked((uint)descriptorLayouts.Length);
        if (descriptorLayouts.Length == 0 || _programDescriptorBindings.Count == 0)
            return false;

        int dynamicOffsetCount = CountDynamicUniformOffsets(descriptorSetLimit);
        if (dynamicOffsetCount > 64)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                Data.Name,
                "descriptor-set",
                "<dynamic-offsets>",
                0,
                0,
                skippedDraw: bindPoint == PipelineBindPoint.Graphics,
                skippedDispatch: bindPoint == PipelineBindPoint.Compute,
                $"descriptor binding requires {dynamicOffsetCount} dynamic offsets; the bounded limit is 64");
            return false;
        }

        Span<uint> dynamicOffsets = stackalloc uint[dynamicOffsetCount];
        dynamicOffsets.Clear();

        if (reusableDescriptorBindingKey != 0UL && BackendContext.Resources.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap)
        {
            ulong preparedSchemaFingerprint = ComputeComputeDescriptorSchemaFingerprint(
                descriptorLayouts,
                descriptorSetLimit);
            if (!ProgramCreationPort.TryGetPreparedComputeDescriptorSets(
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
                    ProgramCreationPort.DescribePreparedComputeDescriptorBindings(
                        imageIndex,
                        preparedSchemaFingerprint));
                RecordComputeDescriptorFailure(
                    default,
                    "prepared descriptor sets were not published before recording",
                    skippedDispatch: true);
                return false;
            }

            recording.Commands.BindDescriptorSetsTracked(
                recording.CommandBuffer,
                bindPoint,
                _pipelineLayout,
                0,
                preparedDescriptorSets,
                dynamicOffsets);
            boundDescriptorSets = preparedDescriptorSets;
            return true;
        }

        if (!TryBuildComputeDescriptorScratch(
                imageIndex,
                snapshot,
                reusableDescriptorBindingKey,
                descriptorSetLimit,
                reportFailures: true))
            return false;
        VulkanComputeDescriptorScratchBuilder scratch = _computeDescriptorScratch;

        if (scratch.WriteCount == 0)
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

        PendingDescriptorWrite[] pendingWriteArray = scratch.Writes;
        DescriptorBufferInfo[] bufferArray = scratch.Buffers;
        DescriptorImageInfo[] imageArray = scratch.Images;
        BufferView[] texelArray = scratch.Texels;

        if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
        {
            DescriptorHeapPushDataPayload payload = VulkanDescriptorManager.CreateHeapPushDataPayload(_descriptorHeapLayout);
            fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
            fixed (DescriptorImageInfo* imagePtr = imageArray)
            fixed (BufferView* texelPtr = texelArray)
            {
                for (int i = 0; i < scratch.WriteCount; i++)
                {
                    PendingDescriptorWrite pending = pendingWriteArray[i];
                    DescriptorBindingInfo binding = FindDescriptorBinding(pending.Set, pending.Binding, pending.DescriptorType);
                    bool wrote;
                    string heapReason;
                    switch (pending.Source)
                    {
                        case PendingDescriptorSource.Buffer:
                            wrote = BackendContext.Resources.DescriptorLifetime.TryWriteDescriptorHeapBinding(this, binding, payload, bufferPtr + pending.SourceStartIndex, null, null, pending.DescriptorCount, out heapReason);
                            break;
                        case PendingDescriptorSource.Image:
                            wrote = BackendContext.Resources.DescriptorLifetime.TryWriteDescriptorHeapBinding(this, binding, payload, null, imagePtr + pending.SourceStartIndex, null, pending.DescriptorCount, out heapReason);
                            break;
                        case PendingDescriptorSource.TexelBuffer:
                            wrote = BackendContext.Resources.DescriptorLifetime.TryWriteDescriptorHeapBinding(this, binding, payload, null, null, texelPtr + pending.SourceStartIndex, pending.DescriptorCount, out heapReason);
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

                if (!recording.Commands.TryPushProgramDescriptorHeapData(recording.CommandBuffer, this, payload))
                {
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                        Data.Name,
                        "descriptor-heap",
                        "<compute-push>",
                        0,
                        0,
                        skippedDraw: false,
                        skippedDispatch: true,
                        "command authority rejected descriptor heap push");
                    return false;
                }
            }

            return true;
        }

        ulong schemaFingerprint = ComputeComputeDescriptorSchemaFingerprint(
            descriptorLayouts,
            descriptorSetLimit);
        ulong bindingFingerprint = ComputeComputeDescriptorBindingFingerprint(pendingWriteArray.AsSpan(0, scratch.WriteCount), bufferArray.AsSpan(0, scratch.BufferCount), imageArray.AsSpan(0, scratch.ImageCount), texelArray.AsSpan(0, scratch.TexelCount));
        ulong cacheBindingFingerprint = reusableDescriptorBindingKey == 0UL ? bindingFingerprint : reusableDescriptorBindingKey;
        if (!ProgramCreationPort.TryGetOrCreateComputeDescriptorSets(
            imageIndex,
            schemaFingerprint,
            cacheBindingFingerprint,
            descriptorLayouts,
            scratch.PoolSizeArray,
            scratch.PoolSizeCount,
            DescriptorLayoutsUseUpdateAfterBind(descriptorLayouts.Length),
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
            UpdateComputeDescriptorSets(descriptorSets, scratch);

        recording.Commands.BindDescriptorSetsTracked(
            recording.CommandBuffer,
            bindPoint,
            _pipelineLayout,
            0,
            descriptorSets,
            dynamicOffsets);

        boundDescriptorSets = descriptorSets;

        return true;
    }

    private void UpdateComputeDescriptorSets(DescriptorSet[] descriptorSets, VulkanComputeDescriptorScratchBuilder scratch)
    {
        PendingDescriptorWrite[] pendingWrites = scratch.Writes;
        DescriptorBufferInfo[] bufferArray = scratch.Buffers;
        DescriptorImageInfo[] imageArray = scratch.Images;
        BufferView[] texelArray = scratch.Texels;
        scratch.EnsureNativeWriteCapacity(scratch.WriteCount);
        WriteDescriptorSet[] writeArray = scratch.NativeWrites;
        for (int i = 0; i < scratch.WriteCount; i++)
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
            for (int i = 0; i < scratch.WriteCount; i++)
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

            if (!TryUpdateComputeDescriptorSetsWithTemplates(descriptorSets, writeArray.AsSpan(0, scratch.WriteCount)))
                BackendContext.Resources.DescriptorLifetime.UpdateDescriptorSets((uint)scratch.WriteCount, writePtr);
            BackendContext.Resources.DescriptorLifetime.RecordTableGeneration();
        }
    }

    private bool TryBuildComputeDescriptorScratch(
        uint imageIndex,
        ComputeDispatchSnapshot snapshot,
        ulong bindingKey,
        uint descriptorSetLimit,
        bool reportFailures)
    {
        VulkanComputeDescriptorScratchBuilder scratch = _computeDescriptorScratch;
        scratch.Reset();
        bool unresolved = false;
        for (int index = 0; index < _programDescriptorBindings.Count; index++)
        {
            DescriptorBindingInfo binding = _programDescriptorBindings[index];
            scratch.RecordScanned();
            if (binding.Set >= descriptorSetLimit)
                continue;
            uint count = Math.Max(binding.Count, 1u);
            scratch.AddPoolSize(binding.DescriptorType, count);
            if (binding.DescriptorType is
                DescriptorType.UniformBuffer or
                DescriptorType.UniformBufferDynamic or
                DescriptorType.StorageBuffer or
                DescriptorType.StorageBufferDynamic)
            {
                if (!TryResolveComputeBuffer(binding, imageIndex, snapshot, bindingKey, out DescriptorBufferInfo info))
                    unresolved = true;
                else
                {
                    int start = scratch.BufferCount;
                    scratch.AddBuffer(in info, count);
                    scratch.AddWrite(binding.Set, binding.Binding, binding.DescriptorType, count, PendingDescriptorSource.Buffer, start);
                }
            }
            else if (binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler or DescriptorType.StorageImage)
            {
                if (!TryResolveComputeImage(binding, snapshot, out DescriptorImageInfo info))
                    unresolved = true;
                else
                {
                    int start = scratch.ImageCount;
                    scratch.AddImage(in info, count);
                    scratch.AddWrite(binding.Set, binding.Binding, binding.DescriptorType, count, PendingDescriptorSource.Image, start);
                }
            }
            else if (binding.DescriptorType is DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer)
            {
                if (!TryResolveComputeTexelBuffer(binding, snapshot, out BufferView view))
                    unresolved = true;
                else
                {
                    int start = scratch.TexelCount;
                    scratch.AddTexel(in view, count);
                    scratch.AddWrite(binding.Set, binding.Binding, binding.DescriptorType, count, PendingDescriptorSource.TexelBuffer, start);
                }
            }
        }

        if (!unresolved)
            return scratch.WriteCount != 0;
        if (reportFailures)
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(Data.Name, "descriptor-set", "<compute-required-binding>", 0, 0, false, true, "compute descriptor build had unresolved required bindings");
        return false;
    }

    private static PushConstantRange CreateCommonPushConstantRange()
        => new()
        {
            StageFlags = VulkanPipelineManager.CommonPushConstantStages,
            Offset = 0,
            Size = VulkanPipelineManager.CommonPushConstantByteSize
        };

    private bool TryUpdateComputeDescriptorSetsWithTemplates(DescriptorSet[] descriptorSets, ReadOnlySpan<WriteDescriptorSet> writeArray)
    {
        if (RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template)
            return false;

        if (_descriptorSetLayouts.Length < descriptorSets.Length)
            return false;

        for (int setIndex = 0; setIndex < descriptorSets.Length; setIndex++)
        {
            int first = -1;
            int count = 0;
            for (int i = 0; i < writeArray.Length; i++)
                if (writeArray[i].DstSet.Handle == descriptorSets[setIndex].Handle)
                {
                    if (first < 0)
                        first = i;
                    count++;
                }

            if (count == 0)
                continue;

            if (!BackendContext.Resources.DescriptorLifetime.TryUpdateDescriptorSetWithTemplate(
                descriptorSets[setIndex],
                _descriptorSetLayouts[setIndex],
                PipelineBindPoint.Compute,
                _pipelineLayout,
                (uint)setIndex,
                writeArray.Slice(first, count)))
            {
                return false;
            }
        }

        return true;
    }

    private ulong ComputeComputeDescriptorSchemaFingerprint(
        IReadOnlyList<DescriptorSetLayout> descriptorLayouts,
        uint descriptorSetLimit)
    {
        ulong hash = 1469598103934665603UL;

        static void Mix(ref ulong value, ulong part)
        {
            value ^= part;
            value *= 1099511628211UL;
        }

        foreach (DescriptorBindingInfo binding in _programDescriptorBindings.OrderBy(b => b.Set).ThenBy(b => b.Binding))
        {
            if (binding.Set >= descriptorSetLimit)
                continue;
            Mix(ref hash, binding.Set);
            Mix(ref hash, binding.Binding);
            Mix(ref hash, (ulong)binding.DescriptorType);
            Mix(ref hash, binding.Count);
            Mix(ref hash, (ulong)binding.StageFlags);
        }

        foreach (DescriptorSetLayout layout in descriptorLayouts)
            Mix(ref hash, layout.Handle);

        return hash;
    }

    private bool DescriptorLayoutsUseUpdateAfterBind(int descriptorLayoutCount)
    {
        int count = Math.Min(descriptorLayoutCount, _descriptorSetUsesUpdateAfterBind.Length);
        for (int index = 0; index < count; index++)
            if (_descriptorSetUsesUpdateAfterBind[index])
                return true;
        return false;
    }

    private int CountDynamicUniformOffsets(uint descriptorSetLimit)
    {
        int count = 0;
        for (int bindingIndex = 0; bindingIndex < _programDescriptorBindings.Count; bindingIndex++)
        {
            DescriptorBindingInfo binding = _programDescriptorBindings[bindingIndex];
            if (binding.Set >= descriptorSetLimit ||
                binding.DescriptorType is not (DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic))
            {
                continue;
            }

            count = checked(count + checked((int)VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding)));
        }

        return count;
    }

    private ulong ComputeComputeDescriptorBindingFingerprint(
        ReadOnlySpan<PendingDescriptorWrite> writes,
        ReadOnlySpan<DescriptorBufferInfo> buffers,
        ReadOnlySpan<DescriptorImageInfo> images,
        ReadOnlySpan<BufferView> texelViews)
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
                            if (BackendContext.Resources.Images.TryGetBackingImage(info.ImageView, out Image backingImage))
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

        if ((binding.DescriptorType is DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic) &&
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
            (binding.DescriptorType is DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic) &&
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
        in VulkanProgramPlannerRequest planner,
        uint imageIndex,
        ComputeDispatchSnapshot snapshot,
        ulong reusableDescriptorBindingKey,
        bool excludeGlobalTextureArray = false)
    {
        if (excludeGlobalTextureArray && !_canBindGlobalTextureArraySeparately)
            return false;

        DescriptorSetLayout[] descriptorLayouts = excludeGlobalTextureArray
            ? _descriptorSetLayoutsBeforeGlobalMaterial
            : _descriptorSetLayouts;
        uint descriptorSetLimit = checked((uint)descriptorLayouts.Length);
        if (descriptorLayouts.Length == 0 || _programDescriptorBindings.Count == 0)
            return true;

        foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
        {
            if (binding.Set >= descriptorSetLimit ||
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

        if (reusableDescriptorBindingKey == 0UL || BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
            return true;

        return TryRefreshReusableComputeDescriptorSets(
            planner,
            imageIndex,
            snapshot,
            reusableDescriptorBindingKey,
            descriptorLayouts,
            descriptorSetLimit);
    }
    internal bool TryRefreshReusableComputeDispatchFrameData(in VulkanProgramPlannerRequest planner, uint imageIndex, ComputeDispatchSnapshot snapshot, ulong reusableDescriptorBindingKey)
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

        return TryRefreshReusableComputeDescriptorSets(
            planner,
            imageIndex,
            snapshot,
            reusableDescriptorBindingKey,
            _descriptorSetLayouts,
            checked((uint)_descriptorSetLayouts.Length));
    }

    private bool TryRefreshReusableComputeDescriptorSets(
        in VulkanProgramPlannerRequest planner,
        uint imageIndex,
        ComputeDispatchSnapshot snapshot,
        ulong reusableDescriptorBindingKey,
        DescriptorSetLayout[] descriptorLayouts,
        uint descriptorSetLimit)
    {
        if (reusableDescriptorBindingKey == 0UL)
            return true;

        snapshot.ResolvePublishedResourceSignatures(
            planner.DescriptorViewFamilyIdentity,
            out _,
            out ulong resourceSignature);
        ulong schemaFingerprint = ComputeComputeDescriptorSchemaFingerprint(
            descriptorLayouts,
            descriptorSetLimit);
        (uint ImageIndex, ulong SchemaFingerprint, ulong BindingKey) refreshKey =
            (imageIndex, schemaFingerprint, reusableDescriptorBindingKey);
        if (snapshot.HasPublishedBindingLayoutSignatures &&
            _reusableComputeDescriptorResourceSignatures.TryGetValue(
                refreshKey,
                out ulong publishedResourceSignature) &&
            publishedResourceSignature == resourceSignature &&
            ProgramCreationPort.TryGetPreparedComputeDescriptorSets(
                imageIndex,
                schemaFingerprint,
                reusableDescriptorBindingKey,
                out _))
        {
            return true;
        }

        if (!TryBuildComputeDescriptorScratch(
                imageIndex,
                snapshot,
                reusableDescriptorBindingKey,
                descriptorSetLimit,
                reportFailures: true))
            return false;

        VulkanComputeDescriptorScratchBuilder scratch = _computeDescriptorScratch;
        if (!ProgramCreationPort.TryGetOrCreateComputeDescriptorSets(
            imageIndex,
            schemaFingerprint,
            reusableDescriptorBindingKey,
            descriptorLayouts,
            scratch.PoolSizeArray,
            scratch.PoolSizeCount,
            _descriptorSetsRequireUpdateAfterBind,
            out DescriptorSet[] descriptorSets,
            out _))
        {
            return false;
        }

        UpdateComputeDescriptorSets(descriptorSets, scratch);
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
        VulkanMappedMemorySlice slice = resource.Slice;
        if (resource.Size < size ||
            !BackendContext.Resources.Buffers.TryAcquireWrite(BackendContext, in slice, out VulkanMappedMemoryWriteLease lease))
            return false;

        using (lease)
        {
            Span<byte> data = lease.Bytes[..checked((int)size)];
            data.Clear();

            IReadOnlyList<AutoUniformMember> members = block.Members;
            for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
                TryWriteAutoUniformMember(data, members[memberIndex], snapshot);
        }

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

        (Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) = BackendContext.Resources.Buffers.Create(BackendContext,
            size,
            BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap
                ? BufferUsageFlags.UniformBufferBit | BufferUsageFlags.ShaderDeviceAddressBit
                : BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            null,
            BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap);

        if (buffer.Handle == 0 || memory.Handle == 0)
        {
            resource = default;
            return false;
        }

        if (!BackendContext.Resources.Buffers.TryCreateMappedSlice(BackendContext, buffer, memory, 0, size, out VulkanMappedMemorySlice slice))
        {
            BackendContext.Resources.Buffers.Retire(buffer, memory, "VkRenderProgram.ComputeUniformBuffer");
            resource = default;
            return false;
        }

        resource = new ComputeUniformBuffer(buffer, memory, size, slice);
        _computeUniformBuffers[key] = resource;
        created = true;
        return true;
    }

    private bool ClearComputeUniformBuffer(ComputeUniformBuffer resource, uint size)
    {
        VulkanMappedMemorySlice slice = resource.Slice;
        if (resource.Size < size ||
            !BackendContext.Resources.Buffers.TryAcquireWrite(BackendContext, in slice, out VulkanMappedMemoryWriteLease lease))
            return false;
        using (lease)
            lease.Bytes[..checked((int)size)].Clear();
        return true;
    }

    private bool TryCreateDescriptorBufferInfo(
        DescriptorBindingInfo binding,
        XRDataBuffer dataBuffer,
        out DescriptorBufferInfo bufferInfo)
    {
        bufferInfo = default;
        bool allowSynchronousBufferUpload = BackendContext.Resources.AllowSynchronousResourceUploads;
        if (WrapperLookup.GetOrCreate(dataBuffer, generateNow: allowSynchronousBufferUpload) is not VkDataBuffer vkBuffer)
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

            if (!TryResolveTextureDescriptor(binding, imageBinding.Texture, includeSampler: false, requiresSampledUsage: false, requiresStorageUsage: true, ImageLayout.General, imageBinding, out imageInfo))
                return false;

            return true;
        }

        if (!snapshot.Samplers.TryGetValue(binding.Binding, out XRTexture? texture))
        {
            // Fallback for shaders that only bind a single sampler but use non-zero binding in source.
            texture = snapshot.Samplers.Count == 1 ? snapshot.Samplers.Values.First() : null;
            if (texture is not null)
            {
                WarnComputeOnce($"Image binding {binding.Binding} ('{binding.Name}') not found in snapshot; using only available sampler '{texture.Name ?? "<unnamed>"}' as fallback.");
                RecordComputeDescriptorFallback(binding);
            }
        }

        if (texture is null)
        {
            imageInfo = BackendContext.Resources.FallbackTexture.GetImageInfo(
                binding.DescriptorType,
                binding.ExpectedImageViewType);
            if (imageInfo.ImageView.Handle != 0)
            {
                RecordComputeDescriptorFallback(binding);
                return true;
            }

            RecordComputeDescriptorFailure(binding, "missing sampled texture and placeholder unavailable", skippedDispatch: false);
            return false;
        }

        bool includeSampler = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler;
        bool requiresSampledUsage = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler or DescriptorType.SampledImage;
        return TryResolveTextureDescriptor(binding, texture, includeSampler, requiresSampledUsage, requiresStorageUsage: false, ImageLayout.ShaderReadOnlyOptimal, storageImageBinding: null, out imageInfo);
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

    private bool TryResolveTextureDescriptor(
        DescriptorBindingInfo binding,
        XRTexture texture,
        bool includeSampler,
        bool requiresSampledUsage,
        bool requiresStorageUsage,
        ImageLayout layout,
        ProgramImageBinding? storageImageBinding,
        out DescriptorImageInfo imageInfo)
    {
        imageInfo = default;
        if (texture is null)
            return false;

        bool allowSynchronousTextureUpload = BackendContext.Resources.AllowSynchronousResourceUploads;
        if (WrapperLookup.GetOrCreate(texture, generateNow: allowSynchronousTextureUpload) is not IVkImageDescriptorSource source)
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

        ImageView descriptorView = storageImageBinding is { } imageBinding
            ? source.GetStorageDescriptorView(imageBinding.Level, imageBinding.Layered, imageBinding.Layer)
            : source.DescriptorView;
        if (descriptorView.Handle == 0)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Descriptor.StorageSubresourceViewUnavailable.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping storage descriptor bind for texture '{0}' because mip {1}, layered={2}, layer={3} has no compatible Vulkan image view.",
                texture.Name ?? texture.GetDescribingName(),
                storageImageBinding?.Level ?? 0,
                storageImageBinding?.Layered ?? false,
                storageImageBinding?.Layer ?? 0);
            return false;
        }

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

        if (!BackendContext.Resources.Images.IsLiveBackedByLiveImage(descriptorView))
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
        if (sampler.Handle != 0 && BackendContext.Resources.Descriptors.IsLiveSampler(sampler))
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

        sampler = BackendContext.Resources.FallbackTexture.GetSampler();
        if (sampler.Handle != 0 && BackendContext.Resources.Descriptors.IsLiveSampler(sampler))
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

        if (WrapperLookup.GetOrCreate(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
            return false;

        texelView = source.DescriptorBufferView;
        return texelView.Handle != 0;
    }

}
