using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

/// <summary>Exposes explicit cache publication for lifetime admission tests.</summary>
public sealed class HierarchyLifetimeProbeTransform : Transform
{
    public Action? Publishing { get; set; }
    public void RequestPublication() => PublishObjectCacheRegistration();

    protected override void PublishObjectCacheRegistrationUnderLock()
    {
        Publishing?.Invoke();
        base.PublishObjectCacheRegistrationUnderLock();
    }
}
