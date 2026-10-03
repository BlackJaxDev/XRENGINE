using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Info;

namespace XREngine.Components.VR;

/// <summary>
/// Cold-path local-avatar layer assignment. Only the two eye masks exclude the layer;
/// spectator, editor, shadows, and capture views keep the complete avatar.
/// The caller must reserve the selected layer for these meshes for the scope lifetime.
/// </summary>
public sealed class VrFirstPersonVisibilityScope : IDisposable
{
    private readonly IRuntimeVrEyeCamera _left, _right;
    private readonly int _leftMask, _rightMask, _layer;
    private readonly List<(RenderInfo3D Info, int Layer)> _meshes = [];
    private bool _disposed;
    private bool _leftChanged, _rightChanged;
    public VrFirstPersonVisibilityScope(IEnumerable<RenderableComponent> avatarRenderables,
        IRuntimeVrEyeCamera left, IRuntimeVrEyeCamera right, int reservedLayer = 30)
    {
        ArgumentNullException.ThrowIfNull(avatarRenderables);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (reservedLayer is < 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(reservedLayer));
        _left = left; _right = right; _layer = reservedLayer;
        _leftMask = left.CullingMask; _rightMask = right.CullingMask;
        try
        {
            left.CullingMask = _leftMask & ~(1 << reservedLayer);
            _leftChanged = true;
            right.CullingMask = _rightMask & ~(1 << reservedLayer);
            _rightChanged = true;
            foreach (RenderableComponent renderable in avatarRenderables)
            foreach (var mesh in renderable.Meshes)
            {
                _meshes.Add((mesh.RenderInfo, mesh.RenderInfo.Layer));
                mesh.RenderInfo.Layer = reservedLayer;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        int bit = 1 << _layer;
        if (_leftChanged) _left.CullingMask = (_left.CullingMask & ~bit) | (_leftMask & bit);
        if (_rightChanged) _right.CullingMask = (_right.CullingMask & ~bit) | (_rightMask & bit);
        foreach (var item in _meshes)
            if (item.Info.Layer == _layer)
                item.Info.Layer = item.Layer;
    }

    /// <summary>Shows the calibration pose in the eyes without changing any global renderable state.</summary>
    public void SetHiddenInEyes(bool hidden)
    {
        if (_disposed) return;
        int bit = 1 << _layer;
        _left.CullingMask = hidden ? _left.CullingMask & ~bit : _left.CullingMask | bit;
        _right.CullingMask = hidden ? _right.CullingMask & ~bit : _right.CullingMask | bit;
    }
}
