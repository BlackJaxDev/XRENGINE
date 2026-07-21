namespace XREngine.Components;

/// <summary>Structural registration intents applied only at world boundaries.</summary>
public enum PhysicsChainWorldCommandKind : byte
{
    Add,
    Remove,
    Retemplate,
    Resize,
    Rebind,
    BackendSwitch,
}
