namespace XREngine.Components;

[Flags]
public enum PhysicsChainInstanceFlags : uint
{
    None = 0u,
    Enabled = 1u << 0,
    Visible = 1u << 1,
    GameplayCritical = 1u << 2,
    CpuMirrorRequested = 1u << 3,
    AutomaticQualityAuthorized = 1u << 4,
}
