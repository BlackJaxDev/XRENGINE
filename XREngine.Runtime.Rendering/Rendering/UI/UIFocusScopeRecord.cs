using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>One open modal focus scope in a published layout tree.</summary>
public readonly record struct UIFocusScopeRecord(
    UIElementIdentity Identity,
    int NodeIndex,
    int SubtreeEndIndex,
    int Depth,
    int PainterOrder)
{
    public bool Contains(int nodeIndex)
        => nodeIndex >= NodeIndex && nodeIndex < SubtreeEndIndex;
}
