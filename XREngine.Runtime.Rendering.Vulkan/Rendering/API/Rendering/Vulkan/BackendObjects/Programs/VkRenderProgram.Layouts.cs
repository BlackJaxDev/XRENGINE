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
    private void BuildProgramInterface()
        => BuildProgramInterfaceWithCompilationProtection();

    private void BuildProgramInterfaceWithCompilationProtection()
    {
        BuildStageLookup();
        DestroyLayoutsAfterPipelineCompileDrain();

        VulkanProgramInterfaceKey key = CaptureProgramInterfaceKey();
        VulkanProgramInterfaceEntry entry = BackendContext.Resources.ProgramInterfaces.Acquire(
            key,
            this,
            CanShareProgramInterface() && key.StageCount == _stageLookup.Count);
        try
        {
            DescriptorLayoutBuildResult result = entry.Descriptors;
            _descriptorSetLayouts = result.Layouts;
            _programDescriptorBindings.Clear();
            _programDescriptorBindings.AddRange(result.Bindings);
            _hasGlobalTextureArrayOnlySet = entry.HasGlobalTextureArrayOnlySet;
            _canBindGlobalTextureArraySeparately = entry.CanBindGlobalTextureArraySeparately;
            _descriptorSetLayoutsBeforeGlobalMaterial = entry.LayoutsBeforeGlobalMaterial;
            _descriptorLayoutFingerprint = entry.LayoutFingerprint;
            _descriptorSchemaFingerprint = entry.SchemaFingerprint;
            _descriptorSetUsesUpdateAfterBind = result.SetUsesUpdateAfterBind;
            _descriptorSetsRequireUpdateAfterBind = result.RequiresUpdateAfterBind;
            _descriptorSetsRequireVariableDescriptorCount = result.RequiresVariableDescriptorCount;
            _externallyOwnedDescriptorSetMask = result.ExternallyOwnedSetMask;
            _descriptorHeapLayout = entry.DescriptorHeapLayout;

            _autoUniformBlocks.Clear();
            _autoUniformBlocksByBinding.Clear();
            _frameMaterialBindingSnapshots.Clear();
            _autoUniformMaterialWritePlans.Clear();
            _frequencyOwnedAutoUniformWritePlans.Clear();
            foreach (AutoUniformBlockInfo block in entry.AutoUniformBlocks)
            {
                _autoUniformBlocks[block.InstanceName] = block;
                _autoUniformBlocksByBinding[(block.Set, block.Binding)] = block;
            }

            ulong linkGeneration = unchecked((ulong)Interlocked.Increment(ref _linkGeneration));
            _bindingSchema = VulkanProgramBindingSchema.Compile(
                linkGeneration,
                _autoUniformBlocks,
                _programDescriptorBindings);
            _pipelineLayout = entry.PipelineLayout;
            _interfaceEntry = entry;
            IsLinked = true;
        }
        catch
        {
            _interfaceEntry = null;
            IsLinked = false;
            BackendContext.Resources.ProgramInterfaces.Release(entry);
            _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
            _pipelineLayout = default;
            throw;
        }
    }

    internal VulkanProgramInterfaceEntry CreateProgramInterfaceEntry(
        VulkanProgramInterfaceKey key,
        ulong generation)
    {
        IEnumerable<DescriptorBindingInfo> shaderBindings = EnumerateShaderDescriptorBindings();
        string programName = Data.Name ?? "UnnamedProgram";
        var result = VulkanProgramUtilities.BuildDescriptorLayoutsShared(
            BackendContext.Resources.Descriptors,
            BackendContext.Resources.AdvancedSceneResources,
            BackendContext.Resources.AdvancedVisibilityResources,
            shaderBindings,
            programName,
            Data.ExternallyOwnedDescriptorSetMask);

        PipelineLayout pipelineLayout = default;
        try
        {
            DescriptorHeapProgramLayout? heapLayout = null;
            if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
            {
                heapLayout = BackendContext.Resources.DescriptorLifetime.CreateDescriptorHeapProgramLayout(
                    result.Bindings,
                    programName,
                    out string descriptorHeapReason,
                    shaderConstantByteCount: VulkanPipelineManager.CommonPushConstantByteSize);
                if (heapLayout is null)
                    throw new InvalidOperationException($"Failed to create Vulkan descriptor heap mapping for program '{programName}': {descriptorHeapReason}");
            }

            List<AutoUniformBlockInfo> blocks = [];
            foreach (EProgramStageMask slot in VulkanProgramUtilities.StageOrder)
            {
                if (!_stageLookup.TryGetValue(slot, out VkShader? shader))
                    continue;
                IReadOnlyList<AutoUniformBlockInfo> shaderBlocks = shader.AutoUniformBlocks;
                for (int blockIndex = 0; blockIndex < shaderBlocks.Count; ++blockIndex)
                    blocks.Add(shaderBlocks[blockIndex]);
            }

            bool hasGlobalTextureArrayOnlySet =
                VulkanBindlessMaterialDescriptors.IsGlobalTextureArrayOnlySet(result.Bindings);
            bool canBindGlobalTextureArraySeparately = hasGlobalTextureArrayOnlySet;
            if (canBindGlobalTextureArraySeparately)
            {
                for (int bindingIndex = 0; bindingIndex < result.Bindings.Count; ++bindingIndex)
                    if (result.Bindings[bindingIndex].Set > VulkanBindlessMaterialDescriptors.TextureArraySet)
                    {
                        canBindGlobalTextureArraySeparately = false;
                        break;
                    }
            }
            DescriptorSetLayout[] layoutsBeforeGlobalMaterial = result.Layouts;
            if (canBindGlobalTextureArraySeparately)
            {
                int ownedSetCount = Math.Min(
                    checked((int)VulkanBindlessMaterialDescriptors.TextureArraySet),
                    result.Layouts.Length);
                layoutsBeforeGlobalMaterial = new DescriptorSetLayout[ownedSetCount];
                Array.Copy(result.Layouts, layoutsBeforeGlobalMaterial, ownedSetCount);
            }

            pipelineLayout = CreatePipelineLayout(result.Layouts);
            return new VulkanProgramInterfaceEntry(
                key,
                generation,
                BackendContext,
                result,
                layoutsBeforeGlobalMaterial,
                hasGlobalTextureArrayOnlySet,
                canBindGlobalTextureArraySeparately,
                [.. blocks],
                heapLayout,
                pipelineLayout,
                ComputeDescriptorLayoutFingerprint(result.Layouts),
                ComputeDescriptorSchemaFingerprint(result.Bindings, result.Layouts.Length));
        }
        catch
        {
            if (pipelineLayout.Handle != 0 &&
                ProgramCreationPort.TryBeginDestroyPipelineLayout(pipelineLayout, "VkRenderProgram.InterfaceBuildFailure"))
                Api!.DestroyPipelineLayout(Device, pipelineLayout, null);
            for (int setIndex = 0; setIndex < result.Layouts.Length; ++setIndex)
                if (!VulkanAdvancedSceneProgramBindingContract.IsExternallyOwnedSet(
                        result.ExternallyOwnedSetMask, (uint)setIndex))
                    BackendContext.Resources.Descriptors.ReleaseProgramDescriptorSetLayout(result.Layouts[setIndex]);
            throw;
        }
    }

    /// <summary>
    /// Computes immutable descriptor layout and schema identities once per successful
    /// link. Draw submission can then compare the cached values without walking every
    /// descriptor binding again.
    /// </summary>
    private static ulong ComputeDescriptorLayoutFingerprint(IReadOnlyList<DescriptorSetLayout> layouts)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        for (int i = 0; i < layouts.Count; i++)
        {
            hash ^= layouts[i].Handle;
            hash *= prime;
        }

        hash ^= unchecked((ulong)layouts.Count);
        return hash * prime;
    }

    private static ulong ComputeDescriptorSchemaFingerprint(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        int setCount)
    {
        VulkanStableHash64 hash = new(schemaVersion: 2);
        hash.Add(setCount);
        for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
        {
            DescriptorBindingInfo binding = bindings[bindingIndex];
            hash.Add(binding.Set);
            hash.Add(binding.Binding);
            hash.Add((int)binding.DescriptorType);
            hash.Add(binding.Count);
            hash.Add((int)binding.StageFlags);
            hash.Add(binding.Name);
        }

        return hash.Value;
    }

    public bool TryGetAutoUniformBlock(string name, out AutoUniformBlockInfo block)
    {
        if (_autoUniformBlocks.TryGetValue(name, out AutoUniformBlockInfo? resolvedBlock) && resolvedBlock is not null)
        {
            block = resolvedBlock;
            return true;
        }

        block = null!;
        return false;
    }

    /// <summary>
    /// Resolves a reflected auto-uniform block through its immutable descriptor
    /// coordinates without inspecting reflection names.
    /// </summary>
    public bool TryGetAutoUniformBlock(
        uint set,
        uint binding,
        out AutoUniformBlockInfo block)
        => _autoUniformBlocksByBinding.TryGetValue((set, binding), out block!);

    /// <summary>
    /// Searches for an auto-uniform block by block name (in addition to
    /// instance name) or by (set, binding) coordinates. This handles the
    /// common case where SPIR-V reflection produces the struct type name
    /// rather than the variable instance name.
    /// </summary>
    public bool TryGetAutoUniformBlockFuzzy(string name, uint set, uint binding, out AutoUniformBlockInfo block)
    {
        // 1. Try exact instance-name match first.
        if (!string.IsNullOrWhiteSpace(name)
            && _autoUniformBlocks.TryGetValue(name, out AutoUniformBlockInfo? resolvedBlock)
            && resolvedBlock is not null)
        {
            block = resolvedBlock;
            return true;
        }

        // 2. Try matching by block name (struct type name from SPIR-V).
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (AutoUniformBlockInfo candidate in _autoUniformBlocks.Values)
            {
                if (string.Equals(candidate.BlockName, name, StringComparison.Ordinal))
                {
                    block = candidate;
                    return true;
                }
            }
        }

        // 3. Fall back to immutable descriptor coordinates.
        if (TryGetAutoUniformBlock(set, binding, out block))
            return true;

        block = default!;
        return false;
    }

    private PipelineLayout CreatePipelineLayout(IReadOnlyList<DescriptorSetLayout> layouts)
    {
        if (!BackendContext.IsDeviceOperational)
            return default;

        PipelineLayout layout;
        if (layouts.Count == 0)
        {
            PushConstantRange pushRange = CreateCommonPushConstantRange();
            PipelineLayoutCreateInfo info = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };
            if (Api!.CreatePipelineLayout(Device, ref info, null, out layout) != Result.Success)
                throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
            ProgramCreationPort.TrackPipelineLayout(layout, $"VkRenderProgram.PipelineLayout#{BindingId}");
            return layout;
        }

        DescriptorSetLayout[] layoutArray = layouts.ToArray();
        fixed (DescriptorSetLayout* layoutPtr = layoutArray)
        {
            PushConstantRange pushRange = CreateCommonPushConstantRange();
            PipelineLayoutCreateInfo info = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)layoutArray.Length,
                PSetLayouts = layoutPtr,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };

            if (Api!.CreatePipelineLayout(Device, ref info, null, out layout) != Result.Success)
                throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
            ProgramCreationPort.TrackPipelineLayout(layout, $"VkRenderProgram.PipelineLayout#{BindingId}");
        }
        return layout;
    }

    private void DestroyLayouts()
    {
        using VulkanPipelineCompilationMutationLease mutationLease =
            ProgramCreationPort.AcquirePipelineCompilationMutationLease(
                this,
                "program layout destruction");
        lock (_linkLock)
            DestroyLayoutsAfterPipelineCompileDrain();
    }

    private void DestroyLayoutsAfterPipelineCompileDrain()
    {
        bool invalidatedPublishedInterface = IsLinked;
        DestroyComputeUniformBuffers();
        _reusableComputeDescriptorResourceSignatures.Clear();

        if (_computePipeline.Handle != 0)
        {
            ProgramCreationPort.RetirePipeline(_computePipeline);
            _computePipeline = default;
        }

        VulkanProgramInterfaceEntry? entry = _interfaceEntry;
        _interfaceEntry = null;
        _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();

        _descriptorSetLayoutsBeforeGlobalMaterial = Array.Empty<DescriptorSetLayout>();
        _hasGlobalTextureArrayOnlySet = false;
        _canBindGlobalTextureArraySeparately = false;

        _pipelineLayout = default;
        if (entry is not null)
            BackendContext.Resources.ProgramInterfaces.Release(entry);

        _programDescriptorBindings.Clear();
        _autoUniformBlocks.Clear();
        _autoUniformBlocksByBinding.Clear();
        _bindingSchema = null;
        _descriptorLayoutFingerprint = 0UL;
        _descriptorSchemaFingerprint = 0UL;
        _descriptorHeapLayout = null;
        _descriptorSetUsesUpdateAfterBind = Array.Empty<bool>();
        _descriptorSetsRequireUpdateAfterBind = false;
        _descriptorSetsRequireVariableDescriptorCount = false;
        _externallyOwnedDescriptorSetMask = 0u;
        _frameMaterialBindingSnapshots.Clear();
        _autoUniformMaterialWritePlans.Clear();
        _frequencyOwnedAutoUniformWritePlans.Clear();
        IsLinked = false;
        if (invalidatedPublishedInterface)
            Interlocked.Increment(ref _linkGeneration);
    }

    private void DestroyComputeUniformBuffers()
    {
        foreach (ComputeUniformBuffer resource in _computeUniformBuffers.Values)
            ReleaseComputeUniformBuffer(resource);

        _computeUniformBuffers.Clear();
    }

    private void ReleaseComputeUniformBuffer(in ComputeUniformBuffer resource)
    {
        if (resource.Buffer.Handle != 0 || resource.Memory.Handle != 0)
            BackendContext.Resources.Buffers.Retire(resource.Buffer, resource.Memory, "VkRenderProgram.ComputeUniformBuffer");
    }

    public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages()
        => GetShaderStages(EProgramStageMask.AllShaderBits);

    public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages(EProgramStageMask mask)
    {
        foreach (EProgramStageMask flag in VulkanProgramUtilities.EnumerateStages(mask))
        {
            // Skip geometry shader stage if the device feature is not enabled.
            if (flag == EProgramStageMask.GeometryShaderBit && !BackendContext.Supports(EVulkanDeviceCapability.GeometryShader))
                continue;

            if (_stageLookup.TryGetValue(flag, out VkShader? shader))
                yield return shader.ShaderStageCreateInfo;
        }
    }

    internal string DescribeShaderStages()
    {
        if (_shaderCache.Count == 0)
            return "<none>";

        return string.Join(", ", _shaderCache.Values
            .OrderBy(static shader => GetShaderStageSortKey(shader.StageFlags))
            .Select(static shader => shader.StageDebugLabel));
    }

    internal void WriteShaderDiagnostics(string reason)
    {
        if (!RenderDiagnosticsFlags.VkDumpShaderOnError)
            return;

        string programName = Data.Name ?? "UnnamedProgram";
        string stageSummary = DescribeShaderStages();
        foreach (VkShader shader in _shaderCache.Values.OrderBy(static shader => GetShaderStageSortKey(shader.StageFlags)))
            shader.WriteRewrittenSourceDiagnostics($"program='{programName}' stages=[{stageSummary}] {reason}");
    }

    private static int GetShaderStageSortKey(ShaderStageFlags stage)
        => stage switch
        {
            ShaderStageFlags.VertexBit => 0,
            ShaderStageFlags.TessellationControlBit => 1,
            ShaderStageFlags.TessellationEvaluationBit => 2,
            ShaderStageFlags.GeometryBit => 3,
            ShaderStageFlags.FragmentBit => 4,
            ShaderStageFlags.ComputeBit => 5,
            ShaderStageFlags.TaskBitNV => 6,
            ShaderStageFlags.MeshBitNV => 7,
            _ => 100,
        };

    private IEnumerable<DescriptorBindingInfo> EnumerateShaderDescriptorBindings()
    {
        // A shared interface must use the same stage order as its cache key.
        // Keep the original reflection order when duplicate stages prevent sharing.
        if (_shaderCache.Count == _stageLookup.Count)
        {
            for (int stageIndex = 0; stageIndex < VulkanProgramUtilities.StageOrderCount; ++stageIndex)
            {
                EProgramStageMask slot = VulkanProgramUtilities.StageAt(stageIndex);
                if (!_stageLookup.TryGetValue(slot, out VkShader? shader))
                    continue;
                foreach (DescriptorBindingInfo binding in shader.DescriptorBindings)
                    yield return binding;
            }
            yield break;
        }

        foreach (VkShader shader in _shaderCache.Values)
        {
            foreach (DescriptorBindingInfo binding in shader.DescriptorBindings)
                yield return binding;
        }
    }

}
