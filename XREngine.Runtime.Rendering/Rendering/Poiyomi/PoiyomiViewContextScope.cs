using System.Numerics;

namespace XREngine.Rendering.Poiyomi;

/// <summary>
/// Restores the previous view classification without allocating.
/// </summary>
public readonly struct PoiyomiViewContextScope : IDisposable
{
    private readonly PoiyomiViewFlags _previousFlags;
    private readonly Vector4 _previousTint;

    internal PoiyomiViewContextScope(PoiyomiViewFlags previousFlags, Vector4 previousTint)
    {
        _previousFlags = previousFlags;
        _previousTint = previousTint;
    }

    public void Dispose()
        => PoiyomiRuntimeAdapters.RestoreViewContext(_previousFlags, _previousTint);
}
