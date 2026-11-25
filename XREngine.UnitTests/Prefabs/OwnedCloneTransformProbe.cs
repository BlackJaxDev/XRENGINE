using XREngine.Scene.Transforms;
using XREngine.Core.Files;

namespace XREngine.UnitTests.Prefabs;

/// <summary>Runs a test callback after an owned transform enters the object cache.</summary>
public sealed class OwnedCloneTransformProbe : Transform
{
    public Action? Publishing;
    public TextFile? Asset;

    protected override void PublishObjectCacheRegistrationUnderLock()
    {
        base.PublishObjectCacheRegistrationUnderLock();
        Publishing?.Invoke();
    }
}
