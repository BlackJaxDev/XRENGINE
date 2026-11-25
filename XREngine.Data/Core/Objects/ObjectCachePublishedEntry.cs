namespace XREngine.Data.Core;

/// <summary>Identifies one exact cache entry inserted while an owned batch completed.</summary>
internal readonly record struct ObjectCachePublishedEntry(Guid Id, XRObjectBase Value);
