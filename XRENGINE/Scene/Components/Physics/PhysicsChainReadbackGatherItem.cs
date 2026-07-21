namespace XREngine.Components;

/// <summary>
/// One typed source element and its exact destination range in a packed
/// transfer buffer.
/// </summary>
public readonly record struct PhysicsChainReadbackGatherItem(
    PhysicsChainReadbackElementKind Kind,
    int SourceIndex,
    int DestinationByteOffset,
    int ByteCount);
