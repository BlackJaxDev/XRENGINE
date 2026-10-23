using XREngine.Components;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>One scroll receiver in a published layout tree.</summary>
public readonly record struct UIScrollRecord(
    XRComponent Owner,
    IUIScrollReceiver Receiver,
    UIElementIdentity Identity,
    int NodeIndex,
    int SubtreeEndIndex,
    int Depth,
    int ComponentIndex)
{
    public bool IsAncestorOf(int nodeIndex)
        => nodeIndex >= NodeIndex && nodeIndex < SubtreeEndIndex;
}
