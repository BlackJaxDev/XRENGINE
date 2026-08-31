using System.Threading;

namespace XREngine.Rendering;

public partial class HybridRenderingManager
{
    [Flags]
    internal enum VulkanGpuCounterDiagnosticFieldGroup
    {
        Counters = 1,
        MaterialScatter = 2,
        IndirectBucket = 4,
    }

    private const int VulkanGpuCounterDiagnosticPassOffset = 1;
    private const int VulkanGpuCounterDiagnosticPassCapacity = 64;
    private static readonly VulkanGpuCounterPassDiagnostic?[] s_vulkanGpuCounterDiagnostics =
        new VulkanGpuCounterPassDiagnostic?[VulkanGpuCounterDiagnosticPassCapacity];

    /// <summary>
    /// Returns the latest opt-in raw GPU counter evidence for each bounded render-pass slot.
    /// Each slot contains only observations from its reported frame and latest pass writer.
    /// </summary>
    public static VulkanGpuCounterPassDiagnostic[] GetVulkanGpuCounterDiagnosticsSnapshot()
    {
        var result = new List<VulkanGpuCounterPassDiagnostic>(VulkanGpuCounterDiagnosticPassCapacity);
        for (int index = 0; index < s_vulkanGpuCounterDiagnostics.Length; index++)
        {
            VulkanGpuCounterPassDiagnostic? snapshot = Volatile.Read(ref s_vulkanGpuCounterDiagnostics[index]);
            if (snapshot is not null)
                result.Add(snapshot);
        }

        return [.. result];
    }

    internal static void RecordVulkanGpuCounterDiagnostics(
        int renderPass,
        string point,
        VulkanGpuCounterDiagnosticFieldGroup fieldGroup,
        uint? culledDrawCount = null,
        uint? culledInstanceCount = null,
        uint? culledOverflowCount = null,
        uint? drawCount = null,
        uint? perViewCandidateCount = null,
        uint? materialScatterInputCount = null,
        uint? materialScatterCulledCount = null,
        uint? materialScatterKeyCount = null,
        uint? materialScatterEmittedCount = null,
        uint? materialScatterRejectedDrawId = null,
        uint? materialScatterRejectedMaterial = null,
        uint? materialScatterRejectedMesh = null,
        uint? materialScatterRejectedAtlas = null,
        uint? materialScatterRejectedBucket = null,
        uint? materialScatterRejectedBounds = null,
        uint? gpuStaticAtlasIndexCount = null,
        uint? gpuDynamicAtlasIndexCount = null,
        uint? gpuStreamingAtlasIndexCount = null,
        uint? gpuStaticAtlasVertexCount = null,
        uint? gpuDynamicAtlasVertexCount = null,
        uint? gpuStreamingAtlasVertexCount = null,
        uint? rejectedAtlasIndexEmpty = null,
        uint? rejectedAtlasVertexEmpty = null,
        uint? rejectedAtlasFirstIndex = null,
        uint? rejectedAtlasIndexRange = null,
        uint? rejectedAtlasBaseVertex = null,
        uint? lastBucketIndex = null,
        uint? lastBucketCount = null,
        uint? lastCommandIndexCount = null,
        uint? lastCommandInstanceCount = null)
    {
        int index = renderPass + VulkanGpuCounterDiagnosticPassOffset;
        if ((uint)index >= VulkanGpuCounterDiagnosticPassCapacity)
            return;

        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        VulkanGpuCounterPassDiagnostic previous = Volatile.Read(ref s_vulkanGpuCounterDiagnostics[index]) ??
            new VulkanGpuCounterPassDiagnostic(renderPass, 0u, string.Empty);
        if (previous.FrameId != frameId)
            previous = new VulkanGpuCounterPassDiagnostic(renderPass, frameId, string.Empty);

        VulkanGpuCounterPassDiagnostic snapshot = previous with
        {
            FrameId = frameId,
            Point = point,
        };
        if ((fieldGroup & VulkanGpuCounterDiagnosticFieldGroup.Counters) != 0)
            snapshot = snapshot with
            {
                CulledDrawCount = culledDrawCount,
                CulledInstanceCount = culledInstanceCount,
                CulledOverflowCount = culledOverflowCount,
                DrawCount = drawCount,
                PerViewCandidateCount = perViewCandidateCount,
            };
        if ((fieldGroup & VulkanGpuCounterDiagnosticFieldGroup.MaterialScatter) != 0)
            snapshot = snapshot with
            {
                MaterialScatterInputCount = materialScatterInputCount,
                MaterialScatterCulledCount = materialScatterCulledCount,
                MaterialScatterKeyCount = materialScatterKeyCount,
                MaterialScatterEmittedCount = materialScatterEmittedCount,
                MaterialScatterRejectedDrawId = materialScatterRejectedDrawId,
                MaterialScatterRejectedMaterial = materialScatterRejectedMaterial,
                MaterialScatterRejectedMesh = materialScatterRejectedMesh,
                MaterialScatterRejectedAtlas = materialScatterRejectedAtlas,
                MaterialScatterRejectedBucket = materialScatterRejectedBucket,
                MaterialScatterRejectedBounds = materialScatterRejectedBounds,
                GpuStaticAtlasIndexCount = gpuStaticAtlasIndexCount,
                GpuDynamicAtlasIndexCount = gpuDynamicAtlasIndexCount,
                GpuStreamingAtlasIndexCount = gpuStreamingAtlasIndexCount,
                GpuStaticAtlasVertexCount = gpuStaticAtlasVertexCount,
                GpuDynamicAtlasVertexCount = gpuDynamicAtlasVertexCount,
                GpuStreamingAtlasVertexCount = gpuStreamingAtlasVertexCount,
                RejectedAtlasIndexEmpty = rejectedAtlasIndexEmpty,
                RejectedAtlasVertexEmpty = rejectedAtlasVertexEmpty,
                RejectedAtlasFirstIndex = rejectedAtlasFirstIndex,
                RejectedAtlasIndexRange = rejectedAtlasIndexRange,
                RejectedAtlasBaseVertex = rejectedAtlasBaseVertex,
            };
        if ((fieldGroup & VulkanGpuCounterDiagnosticFieldGroup.IndirectBucket) != 0)
            snapshot = snapshot with
            {
                LastBucketIndex = lastBucketIndex,
                LastBucketCount = lastBucketCount,
                LastCommandIndexCount = lastCommandIndexCount,
                LastCommandInstanceCount = lastCommandInstanceCount,
            };
        Volatile.Write(ref s_vulkanGpuCounterDiagnostics[index], snapshot);
    }
}
