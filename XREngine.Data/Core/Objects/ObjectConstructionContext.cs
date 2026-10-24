namespace XREngine.Data.Core;

/// <summary>Proves constructor origin while a synchronous owned factory prepares its result.</summary>
public sealed class ObjectConstructionContext
{
    private readonly ObjectConstructionOwnership _ownership;

    internal ObjectConstructionContext(ObjectConstructionOwnership ownership) => _ownership = ownership;

    public bool OwnsAllocation(XRObjectBase value) => _ownership.Contains(value);

    /// <summary>Rejects a borrowed object before a factory changes its identity or graph ownership.</summary>
    public void RequireAllocation(XRObjectBase value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OwnsAllocation(value))
            throw new InvalidOperationException("The object was not allocated by this construction.");
    }
}
