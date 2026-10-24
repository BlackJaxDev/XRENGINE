namespace XREngine.Scene;

/// <summary>Records exact occurrences in one structural list owned by a node or transform.</summary>
public readonly record struct HierarchyListMembership(object Collection, XRWorldObjectBase Owner, int Count, bool IsCurrent);
