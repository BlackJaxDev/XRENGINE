using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private readonly ConditionalWeakTable<XRQuadFrameBuffer, UIWorldQuadFactoryStamp>
        _worldUiQuadFactoryStamps = new();

    private XRQuadFrameBuffer StampWorldUiQuad(XRQuadFrameBuffer quad)
    {
        if (UIWorldQuadFactoryStamp.TryCreate(quad, out UIWorldQuadFactoryStamp? stamp))
            _worldUiQuadFactoryStamps.Add(quad, stamp!);
        return quad;
    }

    internal bool IsCurrentWorldUiFactoryQuad(XRQuadFrameBuffer quad)
        => _worldUiQuadFactoryStamps.TryGetValue(quad, out var stamp) &&
            stamp.IsCurrent(quad);
}
