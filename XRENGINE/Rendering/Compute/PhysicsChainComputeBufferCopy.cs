using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Describes a byte-range copy between two physics-chain GPU resources.
/// </summary>
public readonly record struct PhysicsChainComputeBufferCopy(
    XRDataBuffer Source,
    nint SourceOffset,
    XRDataBuffer Destination,
    nint DestinationOffset,
    nuint ByteCount);
