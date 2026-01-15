using System;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

/// <summary>
/// Handles smooth animated transitions between camera projection types.
/// Supports dolly zoom effect when transitioning from perspective to orthographic.
/// </summary>
public class CameraProjectionTransition : XRBase
{
    /// <summary>
    /// Event fired when the transition completes.
    /// </summary>
    public event Action<CameraProjectionTransition>? TransitionCompleted;

    /// <summary>
    /// Event fired each frame during the transition with the current progress (0-1).
    /// </summary>
    public event Action<CameraProjectionTransition, float>? TransitionProgress;

    private readonly XRCamera _camera;
    private readonly TransformBase? _transform;
    
    // Transition state
    private bool _isTransitioning;
    private float _elapsedTime;
    private float _duration;
    
    // Start values
    private float _startFov;
    private Vector3 _startPosition;
    private float _startNearZ;
    private float _startFarZ;
    
    // Target values  
    private float _targetFov;
    private Vector3 _targetPosition;
    private Type? _targetParameterType;
    private XRCameraParameters? _targetParameters;
    
    // Focus point for dolly zoom
    private float _focusDistance;
    private Vector2 _frustumSizeAtFocus;

    /// <summary>
    /// Whether a transition is currently in progress.
    /// </summary>
    public bool IsTransitioning => _isTransitioning;

    /// <summary>
    /// Current progress of the transition (0 to 1).
    /// </summary>
    public float Progress => _duration > 0 ? Math.Clamp(_elapsedTime / _duration, 0f, 1f) : 1f;

    /// <summary>
    /// Creates a new camera projection transition handler.
    /// </summary>
    /// <param name="camera">The camera to animate.</param>
    /// <param name="transform">The transform to move during dolly zoom. Can be null if no position animation is needed.</param>
    public CameraProjectionTransition(XRCamera camera, TransformBase? transform = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _transform = transform ?? camera.Transform;
    }

    /// <summary>
    /// Starts an animated transition from the current perspective camera to orthographic projection.
    /// Uses a dolly zoom effect to smoothly transition from perspective to near-orthographic view,
    /// then switches to true orthographic projection.
    /// </summary>
    /// <param name="duration">Duration of the transition in seconds.</param>
    /// <param name="focusDistance">Distance from camera to the focus point. Objects at this distance maintain their apparent size.</param>
    /// <param name="minFov">Minimum FOV to animate to before switching to ortho (degrees). Lower = more ortho-like. Default: 2 degrees.</param>
    public void StartPerspectiveToOrthographic(float duration = 1.0f, float focusDistance = 10f, float minFov = 2f)
    {
        if (_camera.Parameters is not XRPerspectiveCameraParameters persp)
        {
            // Not a perspective camera, just switch directly
            var ortho = XRCameraParameters.CreateFromType<XROrthographicCameraParameters>(_camera.Parameters);
            _camera.Parameters = ortho;
            TransitionCompleted?.Invoke(this);
            return;
        }

        _isTransitioning = true;
        _elapsedTime = 0f;
        _duration = Math.Max(0.01f, duration);
        _focusDistance = Math.Max(persp.NearZ + 0.1f, focusDistance);
        
        // Capture start state
        _startFov = persp.VerticalFieldOfView;
        _startPosition = _transform?.WorldTranslation ?? Vector3.Zero;
        _startNearZ = persp.NearZ;
        _startFarZ = persp.FarZ;
        
        // Calculate the frustum size at the focus distance - this is what we want to maintain
        _frustumSizeAtFocus = persp.GetFrustumSizeAtDistance(_focusDistance);
        
        // Target FOV (very small, almost ortho)
        _targetFov = Math.Clamp(minFov, 0.5f, 30f);
        
        // Calculate how far back the camera needs to move to maintain the same frustum size at focus distance
        // with the target FOV
        // frustumHeight = 2 * distance * tan(fov/2)
        // So: distance = frustumHeight / (2 * tan(fov/2))
        float targetFovRad = _targetFov * MathF.PI / 180f;
        float newDistance = _frustumSizeAtFocus.Y / (2f * MathF.Tan(targetFovRad / 2f));
        float distanceDelta = newDistance - _focusDistance;
        
        // Target position: move camera back along its forward direction
        Vector3 forward = _transform?.WorldForward ?? -Vector3.UnitZ;
        _targetPosition = _startPosition - forward * distanceDelta;
        
        // Prepare target orthographic parameters
        // The orthographic size should match the frustum at the focus distance
        _targetParameterType = typeof(XROrthographicCameraParameters);
        var orthoParams = new XROrthographicCameraParameters(
            _frustumSizeAtFocus.X,
            _frustumSizeAtFocus.Y,
            _startNearZ,
            _startFarZ
        );
        // Center the orthographic view so (0,0) is at the camera center, not bottom-left
        orthoParams.SetOriginCentered();
        _targetParameters = orthoParams;
        
        // Register for updates
        Engine.Time.Timer.UpdateFrame += OnUpdate;
    }

    /// <summary>
    /// Starts an animated transition from orthographic to perspective projection.
    /// Reverses the dolly zoom effect.
    /// </summary>
    /// <param name="duration">Duration of the transition in seconds.</param>
    /// <param name="targetFov">Target FOV in degrees for the perspective camera.</param>
    /// <param name="focusDistance">Distance from camera to the focus point after transition.</param>
    public void StartOrthographicToPerspective(float duration = 1.0f, float targetFov = 60f, float focusDistance = 10f)
    {
        if (_camera.Parameters is not XROrthographicCameraParameters ortho)
        {
            // Not an orthographic camera, just switch directly
            var persp = XRCameraParameters.CreateFromType<XRPerspectiveCameraParameters>(_camera.Parameters);
            _camera.Parameters = persp;
            TransitionCompleted?.Invoke(this);
            return;
        }

        _isTransitioning = true;
        _elapsedTime = 0f;
        _duration = Math.Max(0.01f, duration);
        _focusDistance = Math.Max(0.1f, focusDistance);
        
        // Capture start state - use a very small FOV to start (almost ortho)
        _startFov = 2f;
        _startPosition = _transform?.WorldTranslation ?? Vector3.Zero;
        _startNearZ = ortho.NearZ;
        _startFarZ = ortho.FarZ;
        
        // The frustum size we want to maintain
        _frustumSizeAtFocus = new Vector2(ortho.Width, ortho.Height);
        
        // First, switch to perspective with a very small FOV
        float startFovRad = _startFov * MathF.PI / 180f;
        float startDistance = _frustumSizeAtFocus.Y / (2f * MathF.Tan(startFovRad / 2f));
        
        // Move camera to the position it would be at with the small FOV
        Vector3 forward = _transform?.WorldForward ?? -Vector3.UnitZ;
        _startPosition = _startPosition - forward * (startDistance - _focusDistance);
        
        if (_transform is Transform t)
            t.Translation = _startPosition;
        
        // Switch to perspective immediately with small FOV
        var perspParams = new XRPerspectiveCameraParameters(_startFov, null, _startNearZ, _startFarZ);
        _camera.Parameters = perspParams;
        
        // Target state
        _targetFov = Math.Clamp(targetFov, 1f, 170f);
        _targetParameterType = typeof(XRPerspectiveCameraParameters);
        
        // Calculate target position
        float targetFovRad = _targetFov * MathF.PI / 180f;
        float targetDistance = _frustumSizeAtFocus.Y / (2f * MathF.Tan(targetFovRad / 2f));
        float distanceDelta = targetDistance - startDistance;
        _targetPosition = _startPosition - forward * distanceDelta;
        
        _targetParameters = null; // We'll update the existing parameters
        
        Engine.Time.Timer.UpdateFrame += OnUpdate;
    }

    /// <summary>
    /// Cancels the current transition immediately.
    /// </summary>
    public void Cancel()
    {
        if (!_isTransitioning)
            return;
            
        Engine.Time.Timer.UpdateFrame -= OnUpdate;
        _isTransitioning = false;
    }

    private void OnUpdate()
    {
        if (!_isTransitioning)
            return;

        _elapsedTime += Engine.Time.Timer.Update.Delta;
        float t = Math.Clamp(_elapsedTime / _duration, 0f, 1f);
        
        // Use smooth step for easing
        float smoothT = SmoothStep(t);
        
        TransitionProgress?.Invoke(this, t);

        if (_camera.Parameters is XRPerspectiveCameraParameters persp)
        {
            // Interpolate FOV
            float currentFov = Lerp(_startFov, _targetFov, smoothT);
            persp.VerticalFieldOfView = currentFov;
        }

        // Interpolate position
        if (_transform is Transform transform)
        {
            Vector3 currentPos = Vector3.Lerp(_startPosition, _targetPosition, smoothT);
            transform.Translation = currentPos;
        }

        // Check if complete
        if (t >= 1f)
        {
            CompleteTransition();
        }
    }

    private void CompleteTransition()
    {
        Engine.Time.Timer.UpdateFrame -= OnUpdate;
        _isTransitioning = false;

        // Switch to the final target parameters if needed
        if (_targetParameters is not null && _targetParameterType is not null)
        {
            _camera.Parameters = _targetParameters;
        }

        TransitionCompleted?.Invoke(this);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    
    private static float SmoothStep(float t)
    {
        // Hermite interpolation: 3t² - 2t³
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Checks if the given camera parameter types support animated transition.
    /// </summary>
    public static bool CanAnimateTransition(Type fromType, Type toType)
    {
        // Perspective <-> Orthographic transitions are supported
        bool fromPersp = typeof(XRPerspectiveCameraParameters).IsAssignableFrom(fromType);
        bool toOrtho = typeof(XROrthographicCameraParameters).IsAssignableFrom(toType);
        bool fromOrtho = typeof(XROrthographicCameraParameters).IsAssignableFrom(fromType);
        bool toPersp = typeof(XRPerspectiveCameraParameters).IsAssignableFrom(toType);

        return (fromPersp && toOrtho) || (fromOrtho && toPersp);
    }
}
