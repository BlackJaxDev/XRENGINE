using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct DescriptorLayoutBuildResult(DescriptorSetLayout[] Layouts, List<DescriptorBindingInfo> Bindings, bool RequiresUpdateAfterBind, bool RequiresVariableDescriptorCount);

    }
