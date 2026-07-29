using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanBufferCreateTemplate(
    ulong SizeInBytes,
    EBufferTarget Target,
    EBufferUsage Usage)
{
    public static VulkanBufferCreateTemplate FromDescriptor(ulong sizeInBytes, EBufferTarget target, EBufferUsage usage)
        => new(Math.Max(sizeInBytes, 1UL), target, usage);
}

