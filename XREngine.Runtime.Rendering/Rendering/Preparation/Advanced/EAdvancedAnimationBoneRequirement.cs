namespace XREngine.Rendering;

/// <summary>
/// Runtime bone outputs that an authored render-pose LOD tier must preserve.
/// </summary>
[Flags]
public enum EAdvancedAnimationBoneRequirement : uint
{
    None = 0u,
    RuntimeRequired = 1u << 0,
    IkTarget = 1u << 1,
    Attachment = 1u << 2,
    PhysicsChain = 1u << 3,
}
