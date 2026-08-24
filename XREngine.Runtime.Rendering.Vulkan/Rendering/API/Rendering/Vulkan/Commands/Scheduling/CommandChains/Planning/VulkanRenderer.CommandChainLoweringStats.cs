using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct CommandChainLoweringStats(
    int VisibilityPackets,
    int RenderPackets,
    int ChainsScheduled,
    int ChainsRecorded,
    int ChainsReused,
    int ChainsFrameDataRefreshed,
    int VolatileChainsRecorded,
    int SecondaryCommandBuffers,
    string? FirstStructuralDirtyReason,
    string? FirstDescriptorGenerationMismatch,
    string? FirstResourcePlanRevisionMismatch);

