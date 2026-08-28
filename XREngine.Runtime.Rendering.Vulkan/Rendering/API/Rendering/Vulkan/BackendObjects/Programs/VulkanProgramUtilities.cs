using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanProgramUtilities
{
    internal const EProgramStageMask GraphicsStageMask =
        EProgramStageMask.VertexShaderBit |
        EProgramStageMask.TessControlShaderBit |
        EProgramStageMask.TessEvaluationShaderBit |
        EProgramStageMask.GeometryShaderBit |
        EProgramStageMask.FragmentShaderBit |
        EProgramStageMask.MeshShaderBit |
        EProgramStageMask.TaskShaderBit;
    internal static DescriptorLayoutBuildResult BuildDescriptorLayoutsShared(
        VulkanDescriptorManager descriptors,
        VulkanAdvancedSceneResourceRuntime advancedScene,
        IEnumerable<DescriptorBindingInfo> bindings,
        string programName)
    {
        List<DescriptorBindingInfo> reflectedBindings = bindings
            .Select(DescriptorBindingInfo.NormalizeKnownMetadata)
            .Select(NormalizeGraphicsFrameDataBinding)
            .ToList();
        if (VulkanFeatureProfile.EnableDescriptorContractValidation &&
            !VulkanDescriptorContracts.TryValidateContract(reflectedBindings, out string contractError))
        {
            throw new InvalidOperationException($"Descriptor contract validation failed for program '{programName}': {contractError}");
        }

        Dictionary<(uint set, uint binding), DescriptorSetLayoutBindingBuilder> builders = new();
        foreach (DescriptorBindingInfo binding in reflectedBindings)
        {
            var key = (binding.Set, binding.Binding);
            if (!builders.TryGetValue(key, out DescriptorSetLayoutBindingBuilder? builder))
            {
                builder = new DescriptorSetLayoutBindingBuilder(binding);
                builders.Add(key, builder);
            }
            else
            {
                builder.Merge(binding);
            }
        }

        if (builders.Count == 0)
            return new DescriptorLayoutBuildResult(
                Array.Empty<DescriptorSetLayout>(),
                new List<DescriptorBindingInfo>(),
                Array.Empty<bool>(),
                false,
                false,
                0u);

        List<DescriptorBindingInfo> mergedBindings = builders.Values
            .OrderBy(b => b.Set)
            .ThenBy(b => b.Binding)
            .Select(b => b.ToDescriptorBindingInfo())
            .ToList();
        uint externallyOwnedSetMask = 0u;
        if (VulkanAdvancedSceneProgramBindingContract.IsCandidate(mergedBindings))
        {
            if (!advancedScene.IsReady)
            {
                throw new InvalidOperationException(
                    $"Advanced Vulkan program '{programName}' cannot link because the native scene-resource runtime is unavailable: {advancedScene.AvailabilityReason}");
            }
            if (!VulkanAdvancedSceneProgramBindingContract.TryValidate(
                    mergedBindings,
                    advancedScene.DescriptorCapacity,
                    out string advancedContractReason))
            {
                throw new InvalidOperationException(
                    $"Advanced Vulkan descriptor ABI validation failed for program '{programName}': {advancedContractReason}.");
            }

            externallyOwnedSetMask =
                VulkanAdvancedSceneProgramBindingContract.ExternallyOwnedSetMask;
        }

        List<DescriptorSetLayout> layouts = new();
        List<bool> setUsesUpdateAfterBind = new();
        bool requiresUpdateAfterBind = false;
        bool requiresVariableDescriptorCount = false;
        uint maxDeclaredSet = builders.Values.Max(b => b.Set);
        uint maxSet = Math.Max(maxDeclaredSet, VulkanDescriptorManager.SetTierCount - 1);

        Dictionary<uint, List<DescriptorSetLayoutBindingBuilder>> groupsBySet = builders.Values
            .GroupBy(b => b.Set)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Binding).ToList());

        for (uint setIndex = 0; setIndex <= maxSet; setIndex++)
        {
            if (VulkanAdvancedSceneProgramBindingContract.IsExternallyOwnedSet(
                    externallyOwnedSetMask,
                    setIndex))
            {
                if (!advancedScene.TryGetProgramDescriptorSetLayout(
                        setIndex,
                        out DescriptorSetLayout externalLayout))
                {
                    throw new InvalidOperationException(
                        $"Advanced Vulkan program '{programName}' could not resolve runtime-owned descriptor set {setIndex}.");
                }

                layouts.Add(externalLayout);
                setUsesUpdateAfterBind.Add(false);
                continue;
            }

            DescriptorSetLayoutBinding[] vkBindings = groupsBySet.TryGetValue(setIndex, out List<DescriptorSetLayoutBindingBuilder>? setBuilders)
                ? [.. setBuilders.Select(b => b.ToBinding())]
                : Array.Empty<DescriptorSetLayoutBinding>();

            if (!descriptors.TryAcquireProgramDescriptorSetLayout(
                setIndex,
                vkBindings,
                out DescriptorSetLayout layout,
                out bool usesUpdateAfterBind,
                out bool usesVariableDescriptorCount))
                throw new InvalidOperationException($"Failed to create descriptor set layout for program '{programName}'.");

            requiresUpdateAfterBind |= usesUpdateAfterBind;
            requiresVariableDescriptorCount |= usesVariableDescriptorCount;
            layouts.Add(layout);
            setUsesUpdateAfterBind.Add(usesUpdateAfterBind);
        }

        return new DescriptorLayoutBuildResult(
            layouts.ToArray(),
            mergedBindings,
            setUsesUpdateAfterBind.ToArray(),
            requiresUpdateAfterBind,
            requiresVariableDescriptorCount,
            externallyOwnedSetMask);
    }

    /// <summary>
    /// Resolves the descriptor layout from the immutable descriptor source. The
    /// command encoder remains responsible for the transition itself; wrappers
    /// only publish the layout contract and therefore do not retain a renderer.
    /// </summary>
    internal static ImageLayout ResolveDescriptorImageLayout(
        IVkImageDescriptorSource source,
        DescriptorType descriptorType)
    {
        if (descriptorType == DescriptorType.StorageImage)
            return ImageLayout.General;

        if ((source.DescriptorUsage & ImageUsageFlags.StorageBit) != 0 &&
            (source.DescriptorUsage & ImageUsageFlags.SampledBit) != 0)
        {
            return ImageLayout.General;
        }

        ImageLayout tracked = source.TrackedImageLayout;
        if (tracked is ImageLayout.ShaderReadOnlyOptimal or
            ImageLayout.DepthStencilReadOnlyOptimal or
            ImageLayout.DepthReadOnlyOptimal or
            ImageLayout.StencilReadOnlyOptimal or
            ImageLayout.ReadOnlyOptimal)
        {
            return tracked;
        }

        bool depthOrStencil =
            (source.DescriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            source.DescriptorFormat is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;
        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    private static DescriptorBindingInfo NormalizeGraphicsFrameDataBinding(DescriptorBindingInfo binding)
    {
        bool graphicsUniform = binding.Set == VulkanDescriptorManager.GlobalsSetIndex &&
            binding.DescriptorType == DescriptorType.UniformBuffer &&
            (binding.StageFlags & ShaderStageFlags.ComputeBit) == 0;
        return graphicsUniform
            ? binding with { DescriptorType = DescriptorType.UniformBufferDynamic }
            : binding;
    }

    internal static ReadOnlySpan<EProgramStageMask> StageOrder =>
    [
        EProgramStageMask.TaskShaderBit,
        EProgramStageMask.MeshShaderBit,
        EProgramStageMask.VertexShaderBit,
        EProgramStageMask.TessControlShaderBit,
        EProgramStageMask.TessEvaluationShaderBit,
        EProgramStageMask.GeometryShaderBit,
        EProgramStageMask.FragmentShaderBit,
        EProgramStageMask.ComputeShaderBit,
    ];

    internal static IEnumerable<EProgramStageMask> EnumerateStages(EProgramStageMask mask)
    {
        if (mask.HasFlag(EProgramStageMask.TaskShaderBit))
            yield return EProgramStageMask.TaskShaderBit;
        if (mask.HasFlag(EProgramStageMask.MeshShaderBit))
            yield return EProgramStageMask.MeshShaderBit;
        if (mask.HasFlag(EProgramStageMask.VertexShaderBit))
            yield return EProgramStageMask.VertexShaderBit;
        if (mask.HasFlag(EProgramStageMask.TessControlShaderBit))
            yield return EProgramStageMask.TessControlShaderBit;
        if (mask.HasFlag(EProgramStageMask.TessEvaluationShaderBit))
            yield return EProgramStageMask.TessEvaluationShaderBit;
        if (mask.HasFlag(EProgramStageMask.GeometryShaderBit))
            yield return EProgramStageMask.GeometryShaderBit;
        if (mask.HasFlag(EProgramStageMask.FragmentShaderBit))
            yield return EProgramStageMask.FragmentShaderBit;
        if (mask.HasFlag(EProgramStageMask.ComputeShaderBit))
            yield return EProgramStageMask.ComputeShaderBit;
    }
}
