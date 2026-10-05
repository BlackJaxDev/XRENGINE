namespace XREngine.Core.Files;

/// <summary>Where the bytes behind a cooked payload lease live.</summary>
public enum ECookedPayloadStorage : byte
{
    /// <summary>The lease owns nothing; it was never filled or has been disposed.</summary>
    None = 0,
    /// <summary>A span directly over the memory-mapped archive. Valid only while the archive handle is open.</summary>
    Mapped = 1,
    /// <summary>A pooled managed buffer below the large-object-heap threshold.</summary>
    Pooled = 2,
    /// <summary>Native memory for payloads at or above the large-object-heap threshold.</summary>
    Native = 3,
}
