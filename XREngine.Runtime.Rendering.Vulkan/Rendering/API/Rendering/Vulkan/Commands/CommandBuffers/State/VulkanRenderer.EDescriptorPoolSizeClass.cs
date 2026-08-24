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

/// <summary>
/// Descriptor pool size tiers for transient compute pool allocation.
/// Replaces the old uniform 8Ãƒâ€” scaling with demand-aware sizing.
/// </summary>
internal enum EDescriptorPoolSizeClass : byte
{
    /// <summary>Simple shaders with few bindings (shadow, single-texture compute). Scale=4Ãƒâ€”, base=16.</summary>
    Small = 0,
    /// <summary>Standard compute/material passes (3-8 bindings). Scale=8Ãƒâ€”, base=32.</summary>
    Medium = 1,
    /// <summary>Complex passes with many bindings (deferred lighting, multi-texture). Scale=16Ãƒâ€”, base=64.</summary>
    Large = 2,
}

