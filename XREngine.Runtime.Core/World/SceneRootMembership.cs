namespace XREngine.Scene;

/// <summary>Captures exact root occurrences in one scene collection incarnation.</summary>
public readonly record struct SceneRootMembership(SceneRootCollection Collection, int Count)
{
    public XRScene Scene => Collection.Owner;
}
