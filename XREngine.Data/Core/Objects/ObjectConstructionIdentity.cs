namespace XREngine.Data.Core;

/// <summary>Stores an object identity and its admitted lifetime revision.</summary>
public readonly record struct ObjectConstructionIdentity(XRObjectBase Target, Guid Id, long LifetimeRevision)
{
    /// <summary>Reads current values only. This does not acquire lifetime admission or exclude concurrent writers.</summary>
    public static ObjectConstructionIdentity Capture(XRObjectBase target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(target, target.ID, target.LifetimeRevision);
    }

    /// <summary>Checks values only. Callers must hold admission when they need a stable proof.</summary>
    public bool IsCurrent => Target.ID == Id && Target.LifetimeRevision == LifetimeRevision;
}
