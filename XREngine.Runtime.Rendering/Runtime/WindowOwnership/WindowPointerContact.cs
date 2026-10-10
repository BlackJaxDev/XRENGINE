namespace XREngine.Rendering;

/// <summary>A host contact transition, with the same top-left pixel convention as mouse input.</summary>
public readonly record struct WindowPointerContact(int Id, EPointerContactPhase Phase, float X, float Y, ulong Sequence = 0);
