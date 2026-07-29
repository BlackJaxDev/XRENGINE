using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Pipeline-neutral token for one prepared world frame. Desktop and eye
/// pipelines acquire the same token while owning independent view outputs,
/// command recordings, and temporal histories.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedPreparationPublication(
    ulong FrameId,
    ulong PublicationGeneration,
    uint SceneIdentity,
    uint DrawCount,
    uint DeformationJobCount,
    uint DeformationDispatchCount,
    uint VisibilityViewCount,
    uint IndirectRangeCount,
    uint VisibleFallbackCount,
    ulong DeformedVertexCount,
    uint DeformationUploadBytes,
    uint UploadCopyRangeCount,
    EAdvancedPreparationConsumer Consumers,
    bool RequiresCpuReadback,
    bool WarmedManagedAllocationFree,
    bool GpuResourcesPublished,
    bool AggregateDispatchExecuted,
    RuntimeGraphicsApiKind Backend,
    double DeformationGpuMilliseconds);
