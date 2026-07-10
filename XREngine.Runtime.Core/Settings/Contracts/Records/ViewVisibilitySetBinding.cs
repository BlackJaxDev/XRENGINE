using System.Runtime.CompilerServices;

namespace XREngine;

public readonly record struct ViewVisibilitySetBinding(
    int VisibilityGroupIndex,
    int VisibilitySetIdentity,
    ulong Generation,
    bool IsImmutable,
    string? DebugName)
{
    public bool IsBound => VisibilitySetIdentity != 0;

    public static ViewVisibilitySetBinding Unbound(int visibilityGroupIndex)
        => new(visibilityGroupIndex, 0, 0UL, true, null);

    public static ViewVisibilitySetBinding Create(
        int visibilityGroupIndex,
        object visibleSet,
        ulong generation,
        string? debugName = null)
        => visibleSet is null
            ? throw new ArgumentNullException(nameof(visibleSet))
            : new(
                visibilityGroupIndex,
                RuntimeHelpers.GetHashCode(visibleSet),
                generation,
                IsImmutable: true,
                debugName);
}
