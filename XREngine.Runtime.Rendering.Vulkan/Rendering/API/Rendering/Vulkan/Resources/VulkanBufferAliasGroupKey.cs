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

internal readonly record struct VulkanBufferAliasGroupKey(
    VulkanBufferAliasKey AliasKey,
    RenderResourceLifetime Lifetime,
    string GroupDiscriminator)
{
    public static VulkanBufferAliasGroupKey FromRequest(VulkanBufferAllocationRequest request)
    {
        bool aliasable = request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        string discriminator = aliasable ? "TransientAlias" : request.Name;
        return new VulkanBufferAliasGroupKey(request.AliasKey, request.Lifetime, discriminator);
    }
}

