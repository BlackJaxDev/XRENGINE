using System;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Identifies the imported Unity humanoid body channels supplied for one sample.
    /// </summary>
    [Flags]
    public enum EHumanoidImportedBodySampleChannels : byte
    {
        None = 0,
        PositionX = 1 << 0,
        PositionY = 1 << 1,
        PositionZ = 1 << 2,
        RotationX = 1 << 3,
        RotationY = 1 << 4,
        RotationZ = 1 << 5,
        RotationW = 1 << 6,
        Position = PositionX | PositionY | PositionZ,
        Rotation = RotationX | RotationY | RotationZ | RotationW,
        All = Position | Rotation,
    }
}
