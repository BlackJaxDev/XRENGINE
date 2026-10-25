using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Checks exact component membership without changing the scene node.</summary>
internal static class UITextWriteOwner
{
    internal static bool Contains(SceneNode node, XRComponent component)
    {
        for (int index = 0; index < node.Components.Count; index++)
            if (ReferenceEquals(node.Components[index], component))
                return true;
        return false;
    }
}
