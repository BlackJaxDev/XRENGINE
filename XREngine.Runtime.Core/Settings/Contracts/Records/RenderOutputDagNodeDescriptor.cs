namespace XREngine;

public readonly record struct RenderOutputDagNodeDescriptor(
    ulong StableNodeKey,
    ulong StableOutputKey,
    ERenderOutputDagNodeKind Kind,
    ERenderOutputDataClass DataClass,
    ulong StableViewKey,
    ulong PersistentResourceKey,
    uint MaximumContentAgeFrames,
    bool Cacheable,
    bool Resumable,
    string? DebugName = null);
