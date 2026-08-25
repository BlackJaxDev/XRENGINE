namespace XREngine.Components.Animation;

/// <summary>
/// Identifies independently projected components of a humanoid Body Transform.
/// </summary>
[Flags]
public enum EHumanoidProjectedRootChannels
{
    None = 0,
    PositionXZ = 1 << 0,
    PositionY = 1 << 1,
    RotationYaw = 1 << 2,
}
