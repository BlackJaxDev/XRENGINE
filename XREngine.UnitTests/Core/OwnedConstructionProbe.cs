using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

/// <summary>Exercises constructor and publication failures without a runtime graph.</summary>
internal sealed class OwnedConstructionProbe : XRObjectBase
{
    public OwnedConstructionProbe(bool failConstruction = false)
    {
        if (failConstruction)
            throw new InvalidOperationException("Expected constructor failure.");
    }

    public Action? Publishing;
    public int DestroyCount { get; private set; }
    public int EqualityCallCount { get; private set; }

    public override bool Equals(object? obj)
    {
        EqualityCallCount++;
        return obj is OwnedConstructionProbe;
    }

    public override int GetHashCode() => 0;

    public void RequestPublication() => PublishObjectCacheRegistration();

    protected override void PublishObjectCacheRegistrationUnderLock()
    {
        base.PublishObjectCacheRegistrationUnderLock();
        Publishing?.Invoke();
    }

    protected override void OnDestroying()
    {
        DestroyCount++;
        base.OnDestroying();
    }
}
