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
    private float _setupDeadline;
    private int _updates;
    private bool _setupFailed;

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

        _cameraTransform = null;
        _updates = 0;
        _setupFailed = false;
        _setupDeadline = Engine.Time.Timer.Time() + 5.0f;
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateCameraPose);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateCameraPose);
        Debug.WriteAuxiliaryLog("profile-camera-motion",
            $"Deactivated updates={_updates} setupFailed={_setupFailed} camera={_cameraTransform?.SceneNode?.Name ?? "<none>"}");
        _cameraTransform = null;
        base.OnComponentDeactivated();
    }

    private void UpdateCameraPose()
    {
        Transform? transform = _cameraTransform;
        if (transform is null)
        {
            if (_setupFailed)
                return;
            if (!TryResolveActiveCamera())
            {
                if (Engine.Time.Timer.Time() >= _setupDeadline)
                {
                    _setupFailed = true;
                    Debug.WriteAuxiliaryLog("profile-camera-motion", "Active viewport camera setup failed. Invalidate this profile run.");
                }
                return;
            }
            transform = _cameraTransform!;
        }

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
        _updates++;
    }

    private bool TryResolveActiveCamera()
    {
        // Play mode may replace the bootstrap editor pawn. Resolve the camera
        // actually used by a desktop viewport after initial rendering begins.
        if (RuntimeEngine.Rendering.State.RenderFrameId < 10)
            return false;
        foreach (var window in RuntimeEngine.Windows)
        {
            foreach (var viewport in window.Viewports)
            {
                if (viewport.ActiveCamera?.Transform is not Transform transform)
                    continue;
                _cameraTransform = transform;
                _initialTranslation = transform.Translation;
                _initialRotation = transform.Rotation;
                _startTime = Engine.Time.Timer.Time();
                Debug.WriteAuxiliaryLog("profile-camera-motion", $"Activated camera={transform.SceneNode?.Name}");
                return true;
            }
        }
        return false;
    }
}
