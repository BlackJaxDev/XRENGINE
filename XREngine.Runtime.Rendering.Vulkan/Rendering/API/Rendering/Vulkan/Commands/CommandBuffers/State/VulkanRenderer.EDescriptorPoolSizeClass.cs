using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Descriptor pool size tiers for transient compute pool allocation.
    /// Replaces the old uniform 8Ã— scaling with demand-aware sizing.
    /// </summary>
    private enum EDescriptorPoolSizeClass : byte
    {
        /// <summary>Simple shaders with few bindings (shadow, single-texture compute). Scale=4Ã—, base=16.</summary>
        Small = 0,
        /// <summary>Standard compute/material passes (3-8 bindings). Scale=8Ã—, base=32.</summary>
        Medium = 1,
        /// <summary>Complex passes with many bindings (deferred lighting, multi-texture). Scale=16Ã—, base=64.</summary>
        Large = 2,
    }
}

