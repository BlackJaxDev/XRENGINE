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
    internal static DescriptorLayoutBuildResult BuildDescriptorLayoutsShared(VulkanRenderer renderer, Device device, IEnumerable<DescriptorBindingInfo> bindings, string programName)
    {
        List<DescriptorBindingInfo> reflectedBindings = bindings
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
                false);

        List<DescriptorSetLayout> layouts = new();
        List<bool> setUsesUpdateAfterBind = new();
        bool requiresUpdateAfterBind = false;
        bool requiresVariableDescriptorCount = false;
        uint maxDeclaredSet = builders.Values.Max(b => b.Set);
        uint maxSet = Math.Max(maxDeclaredSet, VulkanRenderer.DescriptorSetTierCount - 1);

        Dictionary<uint, List<DescriptorSetLayoutBindingBuilder>> groupsBySet = builders.Values
            .GroupBy(b => b.Set)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Binding).ToList());

        for (uint setIndex = 0; setIndex <= maxSet; setIndex++)
        {
            DescriptorSetLayoutBinding[] vkBindings = groupsBySet.TryGetValue(setIndex, out List<DescriptorSetLayoutBindingBuilder>? setBuilders)
                ? [.. setBuilders.Select(b => b.ToBinding())]
                : Array.Empty<DescriptorSetLayoutBinding>();

            if (!renderer.TryAcquireCachedDescriptorSetLayout(
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

        List<DescriptorBindingInfo> mergedBindings = builders.Values
            .OrderBy(b => b.Set)
            .ThenBy(b => b.Binding)
            .Select(b => b.ToDescriptorBindingInfo())
            .ToList();

        return new DescriptorLayoutBuildResult(
            layouts.ToArray(),
            mergedBindings,
            setUsesUpdateAfterBind.ToArray(),
            requiresUpdateAfterBind,
            requiresVariableDescriptorCount);
    }

    private static DescriptorBindingInfo NormalizeGraphicsFrameDataBinding(DescriptorBindingInfo binding)
    {
        bool graphicsUniform = binding.Set == VulkanRenderer.DescriptorSetGlobals &&
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