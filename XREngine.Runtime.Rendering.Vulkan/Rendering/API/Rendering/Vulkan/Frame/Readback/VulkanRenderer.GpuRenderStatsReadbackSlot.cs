using Silk.NET.Vulkan;
using XREngine.Rendering.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed class GpuRenderStatsReadbackSlot
{
    public VulkanFrameDataSlice DataSlice;
    public int ArenaSlot;
    public uint ByteCount;
    public uint ElementCount;
    public CommandPool CommandPool;
    public CommandBuffer CommandBuffer;
    public Fence Fence;
    public bool Active;
    public bool PublishDraws;
    public bool PublishTriangles;
    public GpuRenderStatsReadbackKind Kind;
    public string SourceName = string.Empty;
    public ulong SourceHandle;
    public ulong SourceFrameId;
    public GpuDiagnosticReadbackPlanNode PlanNode;
    public VulkanGpuDiagnosticReadbackReservation Reservation;
}
