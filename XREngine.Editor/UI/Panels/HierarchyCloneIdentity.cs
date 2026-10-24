namespace XREngine.Editor;

/// <summary>Pins one structural object's identity when its construction receipt is received.</summary>
internal readonly record struct HierarchyCloneIdentity(XRWorldObjectBase Target, Guid Id);
