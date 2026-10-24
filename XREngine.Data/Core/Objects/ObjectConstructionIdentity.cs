namespace XREngine.Data.Core;

/// <summary>Captures one allocation's identity while its construction guard still owns transfer.</summary>
public readonly record struct ObjectConstructionIdentity(XRObjectBase Target, Guid Id, long LifetimeRevision)
{
    /// <summary>Checks values only. Callers must hold admission when they need a stable proof.</summary>
    public bool IsCurrent => Target.ID == Id && Target.LifetimeRevision == LifetimeRevision;
}
