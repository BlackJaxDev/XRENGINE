using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Prefabs;

/// <summary>Exposes a public lifetime override that owned cleanup cannot assume is synchronous.</summary>
public sealed class OwnedRetirementOverrideTransform : Transform
{
    public int DestructionCalls;

    public override void Destroy(bool now = false)
    {
        DestructionCalls++;
        base.Destroy(now);
    }
}
