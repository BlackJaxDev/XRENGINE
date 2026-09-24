namespace XREngine.Rendering.Commands;

/// <summary>
/// One numeric S13a event. A zero command or collection ID means the event is
/// global or occurred outside a known collection. Detail is event-specific:
/// notification dirty-before (0/1), queue authority (0/1), identity handle
/// count, or first rejected plan index. PropertyNameHash is a deterministic
/// hash of the existing notification name, without retaining a string. The
/// five publication identity/generation fields are populated for successful
/// PublicationCommitted and PublicationReused events.
/// </summary>
public readonly record struct S13aPublicationTraceEvent(
    long Sequence, long Timestamp, int ThreadId,
    S13aPublicationTraceEventKind Kind, uint CommandId,
    long CollectionId, long CollectionCycle, ulong PublicationSequence,
    ulong FrameId, int Detail, S13aRenderCommandProperty PropertyCode,
    uint PropertyNameHash, ulong DatabaseEpoch, ulong FrameGeneration,
    ulong TopologyGeneration, ulong ContentGeneration, ulong LookupGeneration);
