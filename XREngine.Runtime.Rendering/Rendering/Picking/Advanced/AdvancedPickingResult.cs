using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

/// <summary>
/// Canonical editor identity resolved from one asynchronously read visibility pixel.
/// Every table reference retains its generation so consumers never reinterpret a reused slot.
/// </summary>
public readonly record struct AdvancedPickingResult(
    bool IsHit,
    ulong RequestGeneration,
    ulong DatabaseEpoch,
    ulong PublicationSequence,
    AdvancedGpuHandle Draw,
    AdvancedGpuHandle Instance,
    AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Material,
    AdvancedGpuHandle CurrentTransform,
    AdvancedGpuHandle PreviousTransform,
    AdvancedGpuHandle EditorIdentity,
    ulong StableComponentId,
    ulong LogicalMeshId,
    uint SelectionId,
    uint PrimitiveSection,
    EAdvancedGeometryProducer Producer,
    uint PrimitiveId,
    uint MeshletOrClusterId,
    uint LocalPrimitiveId,
    uint ViewIndex,
    RenderInfo? AuthoringRenderInfo)
{
    public static AdvancedPickingResult Miss(
        ulong requestGeneration,
        ulong databaseEpoch,
        ulong publicationSequence,
        uint viewIndex)
        => new(
            false,
            requestGeneration,
            databaseEpoch,
            publicationSequence,
            AdvancedGpuHandle.Invalid,
            AdvancedGpuHandle.Invalid,
            AdvancedGpuHandle.Invalid,
            AdvancedGpuHandle.Invalid,
            AdvancedGpuHandle.Invalid,
            AdvancedGpuHandle.Invalid,
            AdvancedGpuHandle.Invalid,
            0u,
            0u,
            AdvancedVisibilityBufferContract.InvalidWord,
            AdvancedVisibilityBufferContract.InvalidWord,
            EAdvancedGeometryProducer.IndirectIndexed,
            AdvancedVisibilityBufferContract.InvalidWord,
            AdvancedVisibilityBufferContract.InvalidWord,
            AdvancedVisibilityBufferContract.InvalidWord,
            viewIndex,
            null);
}
