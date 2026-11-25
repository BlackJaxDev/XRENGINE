namespace XREngine.Scene.Prefabs;

/// <summary>Describes the continuous owner of an owned hierarchy construction.</summary>
public enum EOwnedHierarchyLifetimeState
{
    Retained,
    Retiring,
    Retired,
    RecoveryRequired,
}
