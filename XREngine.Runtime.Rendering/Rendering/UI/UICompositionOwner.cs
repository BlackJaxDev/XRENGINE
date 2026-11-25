using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Identifies an inherited layer without retaining its live component.</summary>
public readonly record struct UICompositionOwner(UIElementIdentity Identity, EUICompositionLayerKind Kind)
{
    public bool IsExplicit => Identity.IsValid;
}
