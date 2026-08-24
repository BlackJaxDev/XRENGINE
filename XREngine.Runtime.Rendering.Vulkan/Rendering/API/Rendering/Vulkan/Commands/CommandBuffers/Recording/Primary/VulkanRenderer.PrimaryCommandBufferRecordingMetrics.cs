using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Counters and failure details collected while recording a primary command buffer.
/// </summary>
internal struct PrimaryCommandBufferRecordingMetrics
{
    public int DroppedDrawOps;
    public int DroppedComputeOps;
    public int DroppedFrameOps;
    public FrameOpFailureSnapshot? FirstFailure;
    public int ClearCount;
    public int DrawCount;
    public int MeshDrawCount;
    public int IndirectDrawCount;
    public int MeshTaskDispatchCount;
    public int BlitCount;
    public int ComputeCount;
    public int SwapchainClearWrites;
    public int ForcedDiagnosticSwapchainWriters;
    public int FboOnlyDrawOps;
    public int FboOnlyBlitOps;
}

