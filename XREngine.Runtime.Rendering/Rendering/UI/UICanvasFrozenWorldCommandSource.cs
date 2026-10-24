using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Rendering.UI;

/// <summary>Connects a canvas packet to scene collection without registering child render infos.</summary>
internal sealed class UICanvasFrozenWorldCommandSource : IFrozenWorldCommandSource
{
    private readonly UICanvasComponent _canvas;

    internal UICanvasFrozenWorldCommandSource(UICanvasComponent canvas) => _canvas = canvas;

    public bool HasFrozenShadowCasters => _canvas.HasFrozenWorldShadowCasters;

    public ERenderInfoCollectResult CollectFrozenCommands(
        RenderCommandCollection commands,
        IRuntimeCullingCamera? camera,
        IVolume? collectionVolume,
        bool containsOnly) => _canvas.CollectFrozenWorldCommands(
            commands, camera, collectionVolume, containsOnly);
}
