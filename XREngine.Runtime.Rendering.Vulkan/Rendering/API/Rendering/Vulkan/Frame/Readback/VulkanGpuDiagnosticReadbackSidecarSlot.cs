using XREngine.Rendering.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Mutable state held by one fixed diagnostic staging-ring slot.</summary>
internal sealed class VulkanGpuDiagnosticReadbackSidecarSlot
{
    internal int State;
    internal ulong FrameIdentity;
    internal GpuDiagnosticReadbackPlanNode Node;
    // A primary-owned copy uses the graphics submission's timeline rather
    // than allocating a second submit/fence solely for diagnostics.
    internal VulkanFrameDataSlice Slice;
    internal CommandBuffer PrimaryCommandBuffer;
    internal ulong CompletionValue;
}
