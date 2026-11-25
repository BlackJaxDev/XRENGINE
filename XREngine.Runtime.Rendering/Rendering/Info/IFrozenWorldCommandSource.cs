using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Info;

/// <summary>Supplies immutable world commands for one scene collection.</summary>
internal interface IFrozenWorldCommandSource
{
    /// <summary>Gets whether a shadow view can collect a frozen mesh command.</summary>
    bool HasFrozenShadowCasters(UIWorldCollectPacketSet? packetSet);

    /// <summary>Collects frozen commands for the exact camera and volume of this view.</summary>
    ERenderInfoCollectResult CollectFrozenCommands(
        RenderCommandCollection commands,
        IRuntimeCullingCamera? camera,
        IVolume? collectionVolume,
        bool containsOnly, UIWorldCollectPacketSet? packetSet);
}
