namespace XREngine.Rendering.UI.State;

/// <summary>Identifies one lifetime of a UI element without keeping the element alive.</summary>
public readonly record struct UIElementIdentity(long Id, long Incarnation)
{
    /// <summary>Gets whether both identity values are positive.</summary>
    public bool IsValid => Id > 0 && Incarnation > 0;
}
