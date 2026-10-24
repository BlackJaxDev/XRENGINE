using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private readonly ConditionalWeakTable<XRQuadFrameBuffer, UIWorldQuadFactoryStamp>
        _worldUiQuadFactoryStamps = new();
    private readonly ConditionalWeakTable<XRMeshRenderer, UIWorldQuadFactoryStamp>
        _worldUiPresentFactoryStamps = new();

    private XRQuadFrameBuffer StampWorldUiQuad(XRQuadFrameBuffer quad)
    {
        if (UIWorldQuadFactoryStamp.TryCreate(quad, out UIWorldQuadFactoryStamp? stamp))
            _worldUiQuadFactoryStamps.Add(quad, stamp!);
        return quad;
    }

    internal bool IsCurrentWorldUiFactoryQuad(XRQuadFrameBuffer quad)
        => _worldUiQuadFactoryStamps.TryGetValue(quad, out var stamp) &&
            stamp.IsCurrent(quad);

    internal void StampWorldUiPresentQuad(XRQuadFrameBuffer quad)
    {
        if (_worldUiPresentFactoryStamps.TryGetValue(quad.FullScreenMesh,
                out _))
            return;
        if (UIWorldQuadFactoryStamp.TryCreate(quad, out var stamp))
            _worldUiPresentFactoryStamps.Add(quad.FullScreenMesh, stamp!);
    }

    internal void ForgetWorldUiPresentQuad(XRQuadFrameBuffer quad)
        => _worldUiPresentFactoryStamps.Remove(quad.FullScreenMesh);

    internal bool IsCurrentWorldUiFactoryPresentQuad(XRMeshRenderer mesh,
        XRMaterial material)
        => _worldUiPresentFactoryStamps.TryGetValue(mesh, out var stamp) &&
            stamp.IsCurrentPresentMesh(mesh, material);
}
