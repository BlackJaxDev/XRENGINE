using System;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    /// <summary>Checks the linked native shader inputs for an owned crop draw.</summary>
    private bool HasExactWorldUiCropProgram(in PendingMeshDraw draw,
        UIPublishedWorldMeshCommand command,
        out VulkanProgramInterfaceEntry? retainedInterface)
    {
        retainedInterface = null;
        if (command.WorldCrop is null ||
            command.WorldCropFragmentShader is not { } fragment ||
            command.MaterialOverride is not { } material ||
            !material.FragmentShaders.Contains(fragment) ||
            draw.PreparedProgram is not { } prepared ||
            !ReferenceEquals(prepared, _program) ||
            draw.PreparedProgramLinkGeneration == 0UL ||
            !prepared.TryRetainProgramInterface(
                draw.PreparedProgramLinkGeneration, out var entry) ||
            entry is null)
            return false;

        bool transferred = false;
        try
        {
            if (entry.Key.StageCount != 2 ||
                !entry.Key.TryGetStage(EProgramStageMask.VertexShaderBit,
                    out VulkanProgramInterfaceStage vertexStage) ||
                !entry.Key.TryGetStage(EProgramStageMask.FragmentShaderBit,
                    out VulkanProgramInterfaceStage fragmentStage) ||
                !ReferenceEquals(fragmentStage.Shader, fragment) ||
                fragmentStage.SourceRevision != command.WorldCropFragmentRevision ||
                fragment.SourceRevision != fragmentStage.SourceRevision ||
                !StageCompiledCurrent(vertexStage,
                    entry.Key.ShaderConfigVersion) ||
                !StageCompiledCurrent(fragmentStage,
                    entry.Key.ShaderConfigVersion) ||
                !UIWorldCropShaderContract.IsCanonicalClippedFragment(fragment))
                return false;

            XRShader compiledVertex = vertexStage.Shader;
            if (compiledVertex.SourceRevision != vertexStage.SourceRevision)
                return false;
            if (command.WorldCropVertexShader is { } explicitVertex)
            {
                if (!ReferenceEquals(compiledVertex, explicitVertex) ||
                    explicitVertex.SourceRevision !=
                        command.WorldCropVertexRevision ||
                    !material.VertexShaders.Contains(explicitVertex) ||
                    !UIWorldCropShaderContract.IsCanonicalVertex(explicitVertex))
                    return false;
            }
            else if (material.VertexShaders.Count != 0 ||
                Data is not XRMeshRenderer.Version<DefaultVertexShaderGenerator>
                    generated ||
                !ReferenceEquals(generated.Parent, command.Mesh) ||
                !string.Equals(compiledVertex.Source?.Text,
                    generated.VertexShaderSource, StringComparison.Ordinal))
                return false;

            retainedInterface = entry;
            transferred = true;
            return true;
        }
        finally
        {
            if (!transferred)
                BackendContext.Resources.ProgramInterfaces.Release(entry);
        }
    }

    /// <summary>Checks the linked stages of a factory-owned output quad.</summary>
    private bool HasExactWorldUiFactoryProgram(in PendingMeshDraw draw,
        XRMaterial material, out VulkanProgramInterfaceEntry? retainedInterface)
    {
        retainedInterface = null;
        if (material.Shaders.Count != 2 ||
            material.VertexShaders.Count != 1 ||
            material.FragmentShaders.Count != 1 ||
            draw.PreparedProgram is not { } prepared ||
            !ReferenceEquals(prepared, _program) ||
            draw.PreparedProgramLinkGeneration == 0UL ||
            !prepared.TryRetainProgramInterface(
                draw.PreparedProgramLinkGeneration, out var entry) ||
            entry is null)
            return false;

        bool transferred = false;
        try
        {
            if (entry.Key.StageCount != 2 ||
                !entry.Key.TryGetStage(EProgramStageMask.VertexShaderBit,
                    out VulkanProgramInterfaceStage vertexStage) ||
                !entry.Key.TryGetStage(EProgramStageMask.FragmentShaderBit,
                    out VulkanProgramInterfaceStage fragmentStage) ||
                !ReferenceEquals(vertexStage.Shader,
                    material.VertexShaders[0]) ||
                !ReferenceEquals(fragmentStage.Shader,
                    material.FragmentShaders[0]) ||
                vertexStage.SourceRevision !=
                    vertexStage.Shader.SourceRevision ||
                fragmentStage.SourceRevision !=
                    fragmentStage.Shader.SourceRevision ||
                !StageCompiledCurrent(vertexStage,
                    entry.Key.ShaderConfigVersion) ||
                !StageCompiledCurrent(fragmentStage,
                    entry.Key.ShaderConfigVersion))
                return false;

            retainedInterface = entry;
            transferred = true;
            return true;
        }
        finally
        {
            if (!transferred)
                BackendContext.Resources.ProgramInterfaces.Release(entry);
        }
    }

    private static bool StageCompiledCurrent(
        in VulkanProgramInterfaceStage stage, int expectedConfig)
        => stage.Wrapper.TryGetCurrentCompiledArtifact(
                out long revision, out int config, out ulong module,
                out string? artifact, out string? entryPoint) &&
            revision == stage.SourceRevision && config == expectedConfig &&
            module == stage.ModuleHandle &&
            string.Equals(artifact, stage.ArtifactIdentity,
                StringComparison.Ordinal) &&
            string.Equals(entryPoint, stage.EntryPoint,
                StringComparison.Ordinal);

    private bool TryBeginWorldUiDescriptorWitness(XRMaterial material,
        int frameDataImageIndex,
        out VulkanWorldUiDescriptorBindingWitness witness)
    {
        witness = default;
        DescriptorAllocation? allocation = _activeDescriptorAllocation;
        if (_program is null || allocation is null ||
            !ReferenceEquals(allocation.Program, _program) ||
            !ReferenceEquals(allocation.Material, material) ||
            !ReferenceEquals(allocation.Sets, _descriptorSets) ||
            allocation.Sets.Length == 0 ||
            allocation.SetCount is <= 0 or > 16 ||
            BackendContext.Resources.Descriptors.Heap.ActiveBackend ==
                EVulkanDescriptorBackend.DescriptorHeap)
            return false;

        int slot = ResolveDescriptorFrameIndex(
            Math.Max(frameDataImageIndex, 0), allocation.Sets.Length);
        if ((uint)slot >= (uint)allocation.Sets.Length ||
            !allocation.IsOwnerGenerationPublished(slot,
                material.BindingResourceVersion))
            return false;

        var sets = allocation.Sets[slot];
        if (sets.Length != allocation.SetCount)
            return false;
        witness = new VulkanWorldUiDescriptorBindingWitness
        {
            Allocation = allocation,
            Program = _program,
            Sets = allocation.Sets,
            Slot = slot,
            ContentGeneration = allocation.ContentGeneration,
            TopologyGeneration = allocation.TopologyGeneration,
            InterfaceGeneration = _program.InterfaceGeneration,
            LayoutHandle = _program.PipelineLayout.Handle,
            MaterialResourceVersion = material.BindingResourceVersion,
            ActiveMask = allocation.ActiveSetMask,
            SetCount = sets.Length,
        };
        return witness.InterfaceGeneration != 0UL &&
            witness.LayoutHandle != 0UL && witness.ActiveMask != 0u;
    }

    private bool TryCaptureWorldUiBoundSet(
        ref VulkanWorldUiDescriptorBindingWitness witness,
        int setIndex, DescriptorSet set,
        ReadOnlySpan<uint> dynamicOffsets)
    {
        if (!witness.Complete && (uint)setIndex < (uint)witness.SetCount &&
            (witness.ActiveMask & (1u << setIndex)) != 0u &&
            dynamicOffsets.Length <= 64 - witness.DynamicOffsetCount &&
            set.Handle != 0UL &&
            BackendContext.Resources.Lifetime.Tracker.PublishedDescriptorSets
                .TryGetValue(set.Handle,
                    out VulkanPublishedDescriptorSetSnapshot? publication) &&
            publication.IsNativePublicationKnown &&
            publication.DescriptorSetLifetimeGeneration != 0UL)
        {
            int offsetStart = witness.DynamicOffsetCount;
            for (int index = 0; index < dynamicOffsets.Length; index++)
                witness.DynamicOffsets[offsetStart + index] =
                    dynamicOffsets[index];
            witness.DynamicOffsetCount += dynamicOffsets.Length;
            witness.SetWitnesses[setIndex] =
                new VulkanWorldUiDescriptorSetWitness((uint)setIndex,
                    set.Handle, publication.DescriptorSetLifetimeGeneration,
                    publication.Generation, publication, offsetStart,
                    dynamicOffsets.Length);
            return true;
        }
        return false;
    }

    private bool WorldUiBoundSetCurrent(
        in VulkanWorldUiDescriptorBindingWitness witness, int setIndex)
    {
        VulkanWorldUiDescriptorSetWitness set = witness.SetWitnesses[setIndex];
        return set.Publication is { } publication &&
            BackendContext.Resources.Lifetime.Tracker.PublishedDescriptorSets
                .TryGetValue(set.Handle,
                    out VulkanPublishedDescriptorSetSnapshot? current) &&
            ReferenceEquals(publication, current) &&
            publication.IsNativePublicationKnown &&
            publication.DescriptorSetLifetimeGeneration ==
                set.LifetimeGeneration &&
            publication.Generation == set.PayloadGeneration;
    }

    private bool HasCurrentWorldUiDescriptorSlot(
        in VulkanWorldUiDescriptorBindingWitness witness,
        XRMaterial material)
    {
        DescriptorAllocation? allocation = witness.Allocation;
        if (!witness.Complete || allocation is null ||
            !ReferenceEquals(_activeDescriptorAllocation, allocation) ||
            !ReferenceEquals(_program, witness.Program) ||
            !ReferenceEquals(_descriptorSets, witness.Sets) ||
            allocation.ContentGeneration != witness.ContentGeneration ||
            allocation.TopologyGeneration != witness.TopologyGeneration ||
            allocation.ActiveSetMask != witness.ActiveMask ||
            _program?.InterfaceGeneration != witness.InterfaceGeneration ||
            _program.PipelineLayout.Handle != witness.LayoutHandle ||
            material.BindingResourceVersion != witness.MaterialResourceVersion ||
            !allocation.IsOwnerGenerationPublished(witness.Slot,
                witness.MaterialResourceVersion))
            return false;
        for (int index = 0; index < witness.SetCount; index++)
            if ((witness.ActiveMask & (1u << index)) != 0u &&
                (!ReferenceEquals(witness.Sets![witness.Slot],
                    allocation.Sets[witness.Slot]) ||
                 allocation.Sets[witness.Slot][index].Handle !=
                    witness.SetWitnesses[index].Handle ||
                 !WorldUiBoundSetCurrent(witness, index)))
                return false;
        return true;
    }

    /// <summary>
    /// Confirms that the pipeline bound before callback-bearing preparation is still the
    /// pipeline that the draw state resolves to, then binds it again. A uniform callback can
    /// record another pipeline on the same command buffer, or change the state that selects
    /// the pipeline. The forced bind makes the draw use the verified pipeline, even when the
    /// tracked bind state does not see the callback's bind. Pipeline creation stays disabled.
    /// </summary>
    private bool ForceWorldUiPipelineAtIssue(
        in DrawRecordingState state,
        PrimitiveTopology topology,
        Pipeline expected)
    {
        if (expected.Handle == 0 ||
            !EnsurePipelineCore(
                state.Material, topology, state.Draw, state.RenderPass,
                state.UseDynamicRendering, state.DynamicRenderingFormats,
                state.PassIndex, state.PassMetadata, state.DepthStencilReadOnly,
                state.PipelineName, allowPipelineCreation: false,
                foregroundRequired: false, out Pipeline current, out _, out _) ||
            current.Handle != expected.Handle)
            return false;
        CommandOperations.ForceWorldUiPipeline(state.CommandBuffer, expected);
        return true;
    }

    /// <summary>Restores the witnessed sets after callback-bearing preparation.</summary>
    private bool ForceWorldUiDescriptorSetsAtIssue(
        CommandBuffer commandBuffer,
        in VulkanWorldUiDescriptorBindingWitness witness,
        XRMaterial material)
    {
        if (!HasCurrentWorldUiDescriptorSlot(in witness, material))
            return false;
        Span<DescriptorSet> oneSet = stackalloc DescriptorSet[1];
        Span<uint> offsets = stackalloc uint[64];
        for (int index = 0; index < witness.SetCount; index++)
        {
            if ((witness.ActiveMask & (1u << index)) == 0u)
                continue;
            VulkanWorldUiDescriptorSetWitness set =
                witness.SetWitnesses[index];
            if (set.OffsetStart < 0 || set.OffsetCount < 0 ||
                set.OffsetStart > witness.DynamicOffsetCount -
                    set.OffsetCount || !WorldUiBoundSetCurrent(witness, index))
                return false;
            oneSet[0] = new DescriptorSet { Handle = set.Handle };
            for (int offset = 0; offset < set.OffsetCount; offset++)
                offsets[offset] = witness.DynamicOffsets[
                    set.OffsetStart + offset];
            CommandOperations.ForceWorldUiDescriptorSets(commandBuffer,
                new PipelineLayout { Handle = witness.LayoutHandle },
                (uint)index,
                oneSet, offsets[..set.OffsetCount]);
        }
        return true;
    }
}
