using System.Threading;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    private VulkanProgramInterfaceEntry? _interfaceEntry;

    /// <summary>Identifies the shared native interface without replacing this wrapper's link identity.</summary>
    internal ulong InterfaceGeneration
        => Volatile.Read(ref _interfaceEntry)?.Generation ?? 0UL;

    internal bool TryRetainProgramInterface(
        ulong expectedLinkGeneration,
        out VulkanProgramInterfaceEntry? entry)
    {
        entry = null;
        if (!IsLinked || LinkGeneration != expectedLinkGeneration)
            return false;
        VulkanProgramInterfaceEntry? candidate = Volatile.Read(ref _interfaceEntry);
        if (candidate is null || !BackendContext.Resources.ProgramInterfaces.TryRetain(candidate))
            return false;
        if (!IsLinked || LinkGeneration != expectedLinkGeneration ||
            !ReferenceEquals(candidate, Volatile.Read(ref _interfaceEntry)))
        {
            BackendContext.Resources.ProgramInterfaces.Release(candidate);
            return false;
        }
        entry = candidate;
        return true;
    }

    internal bool TryRetainProgramInterface(
        PipelineLayout expectedLayout,
        ulong expectedInterfaceGeneration,
        out VulkanProgramInterfaceEntry? entry)
    {
        entry = null;
        VulkanProgramInterfaceEntry? candidate = Volatile.Read(ref _interfaceEntry);
        if (!IsLinked || candidate is null ||
            candidate.Generation != expectedInterfaceGeneration ||
            candidate.PipelineLayout.Handle != expectedLayout.Handle ||
            !BackendContext.Resources.ProgramInterfaces.TryRetain(candidate))
            return false;
        if (!IsLinked || !ReferenceEquals(candidate, Volatile.Read(ref _interfaceEntry)))
        {
            BackendContext.Resources.ProgramInterfaces.Release(candidate);
            return false;
        }
        entry = candidate;
        return true;
    }

    private VulkanProgramInterfaceKey CaptureProgramInterfaceKey()
    {
        VulkanProgramInterfaceStage[] stages = new VulkanProgramInterfaceStage[_stageLookup.Count];
        int stageCount = 0;
        foreach (EProgramStageMask slot in VulkanProgramUtilities.StageOrder)
        {
            if (!_stageLookup.TryGetValue(slot, out VkShader? shader))
                continue;
            VulkanShaderArtifact artifact = shader.LastArtifact ??
                throw new InvalidOperationException("A linked Vulkan shader has no compiled artifact.");
            stages[stageCount++] = new VulkanProgramInterfaceStage(
                slot,
                shader.Data,
                shader,
                shader.Data.SourceRevision,
                shader.ShaderStageCreateInfo.Module.Handle,
                artifact.Identity,
                artifact.EntryPoint);
        }
        if (stageCount != stages.Length)
            Array.Resize(ref stages, stageCount);

        VulkanResourceRuntime resources = BackendContext.Resources;
        resources.AdvancedSceneResources.TryGetProgramDescriptorSetLayout(
            VulkanAdvancedSceneProgramBindingContract.GlobalSetIndex,
            out DescriptorSetLayout globalLayout);
        resources.AdvancedVisibilityResources.TryGetProgramDescriptorSetLayout(
            VulkanAdvancedSceneProgramBindingContract.VisibilitySetIndex,
            out DescriptorSetLayout visibilityLayout);
        resources.AdvancedSceneResources.TryGetProgramDescriptorSetLayout(
            VulkanAdvancedSceneProgramBindingContract.ResourceSetIndex,
            out DescriptorSetLayout resourceLayout);
        PushConstantRange push = CreateCommonPushConstantRange();
        return new VulkanProgramInterfaceKey(
            stages,
            _linkedShaderConfigVersion,
            _linkedUsesVulkanClipDepthRemap,
            _linkedVulkanClipDepthRemapStage,
            _linkedTransformFeedbackLayoutVersion,
            resources.Descriptors.Heap.ActiveBackend,
            Data.ExternallyOwnedDescriptorSetMask,
            globalLayout.Handle,
            visibilityLayout.Handle,
            resourceLayout.Handle,
            push.StageFlags,
            push.Offset,
            push.Size,
            VulkanFeatureProfile.EnableDescriptorContractValidation);
    }

    private bool CanShareProgramInterface()
    {
        // A duplicate stage can change reflection without changing the selected stage.
        if (_shaderCache.Count != _stageLookup.Count)
            return false;
        foreach (XRTransformFeedback feedback in Data.TransformFeedbacks)
        {
            string[]? names = feedback.Names;
            if (names is null)
                continue;
            for (int index = 0; index < names.Length; ++index)
                if (!string.IsNullOrWhiteSpace(names[index]))
                    return false;
        }
        return true;
    }
}
