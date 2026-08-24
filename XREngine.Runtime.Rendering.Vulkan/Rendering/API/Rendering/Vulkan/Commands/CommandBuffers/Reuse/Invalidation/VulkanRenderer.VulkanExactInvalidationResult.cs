using System;
using System.Buffers;
using System.Collections.Concurrent;
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

internal readonly record struct VulkanExactInvalidationResult(
    int ExactVariantsDirtied,
    int ExactCommandChainsDirtied,
    int UnrelatedVariantsPreserved,
    int GlobalFallbackInvalidations);

