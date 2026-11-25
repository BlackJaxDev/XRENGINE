using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Rendering.UI;

/// <summary>Connects a canvas packet to scene collection without registering child render infos.</summary>
internal sealed class UICanvasFrozenWorldCommandSource : IFrozenWorldCommandSource,
    IFrozenWorldPacketSource
{
    private readonly UICanvasComponent _canvas;

    internal UICanvasFrozenWorldCommandSource(UICanvasComponent canvas) => _canvas = canvas;

    public object PacketKey => _canvas;
    public bool IsInWorld(IRuntimeWorldContext expectedWorld)
        => ReferenceEquals(_canvas.SceneNode?.World, expectedWorld);

    public bool TryAcquireCollectPacket(IRuntimeWorldContext expectedWorld,
        out UIPublication? packet)
        => _canvas.TryAcquireFrozenWorldCollectPacket(expectedWorld, out packet);

    public bool HasFrozenShadowCasters(UIWorldCollectPacketSet? packetSet)
        => _canvas.HasFrozenWorldShadowCasters(packetSet);

    public ERenderInfoCollectResult CollectFrozenCommands(
        RenderCommandCollection commands,
        IRuntimeCullingCamera? camera,
        IVolume? collectionVolume,
        bool containsOnly, UIWorldCollectPacketSet? packetSet)
        => _canvas.CollectFrozenWorldCommands(
            commands, camera, collectionVolume, containsOnly, packetSet);
}
