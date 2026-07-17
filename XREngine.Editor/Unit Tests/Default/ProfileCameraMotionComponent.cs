using System.Numerics;
using XREngine.Components;
using XREngine.Data;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Drives a deterministic, bounded camera path for automated rendering profiles.
/// </summary>
public sealed class ProfileCameraMotionComponent : XRComponent
{
    private Transform? _cameraTransform;
    private Vector3 _initialTranslation;
    private Quaternion _initialRotation;
    private float _startTime;

    /// <summary>
    /// Returns whether the active profile requests the automated moving-camera path.
    /// </summary>
    public static bool IsRequested()
    {
        string? profile = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCamera);
        return string.Equals(profile, "Moving", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile, "Orbit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile, "MovingOrbit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile, "Moving-Orbit", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();

        _cameraTransform = Transform as Transform;
        if (_cameraTransform is null)
            return;

        _initialTranslation = _cameraTransform.Translation;
        _initialRotation = _cameraTransform.Rotation;
        _startTime = Engine.Time.Timer.Time();
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateCameraPose);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateCameraPose);
        _cameraTransform = null;
        base.OnComponentDeactivated();
    }

    private void UpdateCameraPose()
    {
        Transform? transform = _cameraTransform;
        if (transform is null)
            return;

        float elapsed = Engine.Time.Timer.Time() - _startTime;
        Vector3 localOffset = new(
            MathF.Sin(elapsed * 0.47f) * 0.80f,
            MathF.Sin(elapsed * 0.31f) * 0.18f,
            MathF.Sin(elapsed * 0.23f) * 0.35f);
        Quaternion localRotation = Quaternion.CreateFromYawPitchRoll(
            MathF.Sin(elapsed * 0.37f) * 0.20f,
            MathF.Sin(elapsed * 0.29f) * 0.08f,
            0.0f);

        transform.Translation = _initialTranslation + Vector3.Transform(localOffset, _initialRotation);
        transform.Rotation = Quaternion.Normalize(_initialRotation * localRotation);
    }
}
