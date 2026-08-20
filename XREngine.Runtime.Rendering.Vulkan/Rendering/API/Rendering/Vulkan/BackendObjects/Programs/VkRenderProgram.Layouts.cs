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
        => ProgramCreationPort.ExecuteWithPipelineCompilationQuiesced(
            BuildProgramInterfaceAfterPipelineCompileDrain,
            $"program interface rebuild for '{Data.Name ?? "<unnamed program>"}'");

    private void BuildProgramInterfaceAfterPipelineCompileDrain()
    {
        BuildStageLookup();
        DestroyLayoutsAfterPipelineCompileDrain();

        IEnumerable<DescriptorBindingInfo> shaderBindings = EnumerateShaderDescriptorBindings();
        string programName = Data.Name ?? "UnnamedProgram";
        var result = VulkanProgramUtilities.BuildDescriptorLayoutsShared(BackendContext.Resources.Descriptors, shaderBindings, programName);

        _descriptorSetLayouts = result.Layouts;
        _programDescriptorBindings.Clear();
        _programDescriptorBindings.AddRange(result.Bindings);
        _hasGlobalTextureArrayOnlySet =
            VulkanBindlessMaterialDescriptors.IsGlobalTextureArrayOnlySet(_programDescriptorBindings);
        _canBindGlobalTextureArraySeparately = _hasGlobalTextureArrayOnlySet;
        if (_canBindGlobalTextureArraySeparately)
        {
            for (int bindingIndex = 0; bindingIndex < _programDescriptorBindings.Count; bindingIndex++)
            {
                if (_programDescriptorBindings[bindingIndex].Set > VulkanBindlessMaterialDescriptors.TextureArraySet)
                {
                    _canBindGlobalTextureArraySeparately = false;
                    break;
                }
            }
        }
        if (_canBindGlobalTextureArraySeparately)
        {
            int ownedSetCount = Math.Min(
                checked((int)VulkanBindlessMaterialDescriptors.TextureArraySet),
                _descriptorSetLayouts.Length);
            _descriptorSetLayoutsBeforeGlobalMaterial = new DescriptorSetLayout[ownedSetCount];
            Array.Copy(
                _descriptorSetLayouts,
                _descriptorSetLayoutsBeforeGlobalMaterial,
                ownedSetCount);
        }
        else
        {
            _descriptorSetLayoutsBeforeGlobalMaterial = _descriptorSetLayouts;
        }
        _descriptorLayoutFingerprint = ComputeDescriptorLayoutFingerprint(_descriptorSetLayouts);
        _descriptorSchemaFingerprint = ComputeDescriptorSchemaFingerprint(
            _programDescriptorBindings,
            _descriptorSetLayouts.Length);
        _descriptorSetUsesUpdateAfterBind = result.SetUsesUpdateAfterBind;
        _descriptorSetsRequireUpdateAfterBind = result.RequiresUpdateAfterBind;
        _descriptorSetsRequireVariableDescriptorCount = result.RequiresVariableDescriptorCount;
        _descriptorHeapLayout = null;
        if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
        {
            _descriptorHeapLayout = BackendContext.Resources.DescriptorLifetime.CreateDescriptorHeapProgramLayout(
                _programDescriptorBindings,
                programName,
                out string descriptorHeapReason);
            if (_descriptorHeapLayout is null)
                throw new InvalidOperationException($"Failed to create Vulkan descriptor heap mapping for program '{programName}': {descriptorHeapReason}");
        }

        _autoUniformBlocks.Clear();
        _autoUniformBlocksByBinding.Clear();
        _frameMaterialBindingSnapshots.Clear();
        _autoUniformMaterialWritePlans.Clear();
        _frequencyOwnedAutoUniformWritePlans.Clear();
        foreach (VkShader shader in _shaderCache.Values)
        {
            IReadOnlyList<AutoUniformBlockInfo> shaderBlocks =
                shader.AutoUniformBlocks;
            for (int blockIndex = 0;
                 blockIndex < shaderBlocks.Count;
                 blockIndex++)
            {
                AutoUniformBlockInfo block = shaderBlocks[blockIndex];
                _autoUniformBlocks[block.InstanceName] = block;
                _autoUniformBlocksByBinding[(block.Set, block.Binding)] = block;
            }
        }

        ulong linkGeneration = unchecked((ulong)Interlocked.Increment(ref _linkGeneration));
        _bindingSchema = VulkanProgramBindingSchema.Compile(
            linkGeneration,
            _autoUniformBlocks,
            _programDescriptorBindings);
        CreatePipelineLayout(_descriptorSetLayouts);
        IsLinked = true;
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

    private void CreatePipelineLayout(IReadOnlyList<DescriptorSetLayout> layouts)
    {
        if (!BackendContext.IsDeviceOperational)
            return;

        DestroyPipelineLayout("VkRenderProgram.CreatePipelineLayout");

        if (layouts.Count == 0)
        {
            PushConstantRange pushRange = CreateCommonPushConstantRange();
            PipelineLayoutCreateInfo info = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };
            if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
            ProgramCreationPort.TrackPipelineLayout(_pipelineLayout, "VkRenderProgram.PipelineLayout");
            return;
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

            if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
            ProgramCreationPort.TrackPipelineLayout(_pipelineLayout, "VkRenderProgram.PipelineLayout");
        }
    }

    private void DestroyLayouts()
    {
        lock (_linkLock)
        {
            ProgramCreationPort.ExecuteWithPipelineCompilationQuiesced(
                DestroyLayoutsAfterPipelineCompileDrain,
                $"pipeline layout mutation for '{Data.Name ?? "<unnamed program>"}'");
        }
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

        if (_descriptorSetLayouts.Length > 0)
        {
            foreach (DescriptorSetLayout layout in _descriptorSetLayouts)
                BackendContext.Resources.Descriptors.ReleaseProgramDescriptorSetLayout(layout);

            _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
        }

        _descriptorSetLayoutsBeforeGlobalMaterial = Array.Empty<DescriptorSetLayout>();
        _hasGlobalTextureArrayOnlySet = false;
        _canBindGlobalTextureArraySeparately = false;

        if (_pipelineLayout.Handle != 0)
            DestroyPipelineLayout("VkRenderProgram.DestroyLayouts");

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
        _frameMaterialBindingSnapshots.Clear();
        _autoUniformMaterialWritePlans.Clear();
        _frequencyOwnedAutoUniformWritePlans.Clear();
        IsLinked = false;
        if (invalidatedPublishedInterface)
            Interlocked.Increment(ref _linkGeneration);
    }

    private void DestroyPipelineLayout(string owner)
    {
        if (_pipelineLayout.Handle == 0)
            return;

        PipelineLayout pipelineLayout = _pipelineLayout;
        _pipelineLayout = default;

        if (ProgramCreationPort.TryBeginDestroyPipelineLayout(pipelineLayout, owner))
            Api!.DestroyPipelineLayout(Device, pipelineLayout, null);
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
        foreach (VkShader shader in _shaderCache.Values)
        {
            foreach (DescriptorBindingInfo binding in shader.DescriptorBindings)
                yield return binding;
        }
    }

}
