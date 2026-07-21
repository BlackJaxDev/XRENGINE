namespace XREngine.Components;

/// <summary>Latest-value runtime intents applied after structural commands.</summary>
public enum PhysicsChainWorldDynamicCommandKind : byte
{
    Root,
    Force,
    Parameters,
    Relevance,
    Quality,
}
