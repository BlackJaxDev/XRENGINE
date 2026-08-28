namespace XREngine.Components;

/// <summary>
/// Transform channels affected by an imported Unity/VRChat constraint.
/// </summary>
[Flags]
public enum TransformConstraintChannels
{
    None = 0,
    PositionX = 1 << 0,
    PositionY = 1 << 1,
    PositionZ = 1 << 2,
    RotationX = 1 << 3,
    RotationY = 1 << 4,
    RotationZ = 1 << 5,
    ScaleX = 1 << 6,
    ScaleY = 1 << 7,
    ScaleZ = 1 << 8,
    Position = PositionX | PositionY | PositionZ,
    Rotation = RotationX | RotationY | RotationZ,
    Scale = ScaleX | ScaleY | ScaleZ,
    Parent = Position | Rotation,
    All = Position | Rotation | Scale,
}
