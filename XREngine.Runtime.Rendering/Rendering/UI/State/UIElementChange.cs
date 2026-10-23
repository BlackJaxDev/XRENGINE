namespace XREngine.Rendering.UI.State;

/// <summary>Records the newest parent and combined work for one element lifetime.</summary>
public readonly record struct UIElementChange(
    UIElementIdentity Element,
    UIElementIdentity Parent,
    UIChangeDomain Domains, bool IsRemoved = false);
