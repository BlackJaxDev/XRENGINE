using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>Owns one fixed-base input set until its native readers retire.</summary>
internal sealed class PhysicsChainInputPage
{
    internal XRDataBuffer<GPUPhysicsChainDispatcher.GPUPerTreeParams>? Headers;
    internal XRDataBuffer<PhysicsChainGpuInstanceMetadata>? Instances;
    internal XRDataBuffer<PhysicsChainGpuTreeWorkItem>? TreeWork;
    internal XRDataBuffer<GPUPhysicsChainDispatcher.GPUColliderData>? Colliders;
    internal XRDataBuffer<PhysicsChainAffineInput>? Transforms;
    internal XRGpuFence? Fence;
    internal AbstractRenderer? ProducerRenderer;
    internal bool Reserved;
    internal bool HasWritten;
    internal bool HasQueuedWork;
    internal bool FailedMarkerRecorded;
    internal bool Quarantined;
    internal bool NativeResourcesRetired;
    internal ulong SubmissionOrdinal;
}
