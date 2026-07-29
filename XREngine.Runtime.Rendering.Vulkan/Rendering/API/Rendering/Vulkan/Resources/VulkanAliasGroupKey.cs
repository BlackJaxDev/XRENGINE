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

internal readonly record struct VulkanAliasGroupKey(
    VulkanAliasKey AliasKey,
    RenderResourceLifetime Lifetime,
    string GroupDiscriminator)
{
    public static VulkanAliasGroupKey FromRequest(VulkanAllocationRequest request)
    {
        bool aliasable = request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        string discriminator = aliasable ? "TransientAlias" : request.Name;
        return new VulkanAliasGroupKey(request.AliasKey, request.Lifetime, discriminator);
    }
}

