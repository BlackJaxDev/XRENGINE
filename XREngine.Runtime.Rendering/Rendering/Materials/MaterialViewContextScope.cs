using System.Numerics;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Restores the previous view classification without allocating.
/// </summary>
public readonly struct MaterialViewContextScope : IDisposable
{
    private readonly MaterialViewFlags _previousFlags;
    private readonly Vector4 _previousTint;

    internal MaterialViewContextScope(MaterialViewFlags previousFlags, Vector4 previousTint)
    {
        _previousFlags = previousFlags;
        _previousTint = previousTint;
    }

    public void Dispose()
        => UberMaterialRuntimeAdapters.RestoreViewContext(_previousFlags, _previousTint);
}
