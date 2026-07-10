using System.Numerics;

namespace XREngine;

public readonly record struct RvcValidationCaptureContract(
    ERvcValidationScene Scene,
    string CameraName,
    Vector2 GazeUv,
    int WarmCacheFrameCount,
    int CaptureFrameIndex,
    bool FixedAnimationTime,
    bool IdenticalSceneState)
{
    public static RvcValidationCaptureContract CreateDefault(ERvcValidationScene scene)
        => new(
            scene,
            "RVC.FixedValidationCamera",
            new Vector2(0.5f, 0.5f),
            WarmCacheFrameCount: 8,
            CaptureFrameIndex: 16,
            FixedAnimationTime: true,
            IdenticalSceneState: true);
}
