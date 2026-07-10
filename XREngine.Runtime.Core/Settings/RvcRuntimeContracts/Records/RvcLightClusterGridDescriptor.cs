using System.Numerics;

namespace XREngine;

public readonly record struct RvcLightClusterGridDescriptor(
    ERvcLightGridSpace Space,
    Vector3 CameraRelativeOrigin,
    Vector3 ExtentsMeters,
    float CellSizeMeters,
    RvcFoveationLightBudget LightBudget)
{
    public static RvcLightClusterGridDescriptor CreateDefault(Vector3 cameraRelativeOrigin)
        => new(
            ERvcLightGridSpace.WorldAlignedCameraRelative,
            cameraRelativeOrigin,
            new Vector3(64.0f, 32.0f, 64.0f),
            CellSizeMeters: 0.5f,
            RvcFoveationLightBudget.Default);
}
