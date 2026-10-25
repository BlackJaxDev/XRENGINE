using System.Numerics;
using XREngine.Scene;
using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

/// <summary>Captures one menu transition before callbacks can replace the request.</summary>
internal readonly record struct UIContextMenuRequest(
    bool Show, bool ClearItems, Vector2 Position, ContextMenuItem[]? Items = null,
    IVirtualListProvider? Provider = null, Action<SceneNode>? ContentBuilder = null,
    float ContentHeight = 0, bool Passive = false, bool SubscribeForDismiss = true,
    float ItemHeight = 0, float SeparatorHeight = 0)
{
    public ObjectConstructionIdentity Owner { get; init; }
    public ObjectConstructionIdentity Node { get; init; }
    public ObjectConstructionIdentity Transform { get; init; }
    public ObjectConstructionIdentity? Parent { get; init; }
}
