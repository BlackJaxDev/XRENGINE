using Silk.NET.OpenXR;
using System;
using System.Numerics;
using System.Threading;
using Debug = XREngine.Debug;
using XrAction = Silk.NET.OpenXR.Action;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Reads the runtime eye gaze through XR_EXT_eye_gaze_interaction and converts it to a
/// per-view render-target UV for eye-tracked foveation.
/// </summary>
public unsafe partial class OpenXRAPI
{
    private const string ExtEyeGazeInteractionExtensionName = "XR_EXT_eye_gaze_interaction";
    private const string EyeGazeInteractionProfilePath = "/interaction_profiles/ext/eye_gaze_interaction";
    private const string EyeGazePoseBindingPath = "/user/eyes_ext/input/gaze_ext/pose";

    /// <summary>
    /// Distance along the gaze ray of the point that each view projects. The runtime gaze has no
    /// vergence depth, so a fixed fixation distance gives each eye a stable, parallax-correct center.
    /// </summary>
    private const float EyeGazeFixationDistanceMeters = 1.0f;

    private XrAction _eyeGazePoseAction;
    private Space _eyeGazeSpace;
    private bool _eyeGazeSystemSupported;
    private ulong _eyeGazeQueriedSystemId;

    // Guarded by _openXrPoseLock.
    private Matrix4x4 _openXrPredEyeGazeLocalPose = Matrix4x4.Identity;
    private int _openXrPredEyeGazeValid;
    private int _openXrPredEyeGazeFrameNumber;

    /// <summary>
    /// True when the runtime enabled XR_EXT_eye_gaze_interaction and the system reports eye gaze support.
    /// </summary>
    private bool IsOpenXrEyeGazeAvailable
    {
        get
        {
            if (!IsInstanceExtensionEnabled(ExtEyeGazeInteractionExtensionName))
                return false;

            if (_eyeGazeQueriedSystemId != _systemId)
                QueryEyeGazeSystemSupport();
            return _eyeGazeSystemSupported;
        }
    }

    private void QueryEyeGazeSystemSupport()
    {
        _eyeGazeSystemSupported = false;
        _eyeGazeQueriedSystemId = _systemId;
        if (_instance.Handle == 0 || _systemId == 0)
            return;

        var eyeGazeProperties = new SystemEyeGazeInteractionPropertiesEXT
        {
            Type = StructureType.SystemEyeGazeInteractionPropertiesExt,
        };
        var properties = new SystemProperties
        {
            Type = StructureType.SystemProperties,
            Next = &eyeGazeProperties,
        };

        Result result = Api.GetSystemProperties(_instance, _systemId, ref properties);
        _eyeGazeSystemSupported = result == Result.Success && eyeGazeProperties.SupportsEyeGazeInteraction != 0;
        Debug.Out($"OpenXR eye gaze: extension enabled, system support={_eyeGazeSystemSupported} (result={result}).");
    }

    private void CreateEyeGazeAction()
    {
        if (!IsOpenXrEyeGazeAvailable)
            return;

        var info = new ActionCreateInfo
        {
            Type = StructureType.ActionCreateInfo,
            ActionType = ActionType.PoseInput,
            CountSubactionPaths = 0,
            SubactionPaths = null,
        };

        WriteUtf8Z(info.ActionName, 64, "eye_gaze_pose");
        WriteUtf8Z(info.LocalizedActionName, 128, "Eye Gaze Pose");

        Result result = Api.CreateAction(_inputActionSet, in info, ref _eyeGazePoseAction);
        if (result != Result.Success)
        {
            Debug.LogWarning($"xrCreateAction(eye_gaze_pose) failed: {result}");
            _eyeGazePoseAction = default;
        }
    }

    private void CreateEyeGazeActionSpace()
    {
        if (_eyeGazePoseAction.Handle == 0)
            return;

        var info = new ActionSpaceCreateInfo
        {
            Type = StructureType.ActionSpaceCreateInfo,
            Action = _eyeGazePoseAction,
            SubactionPath = default,
            PoseInActionSpace = new Posef
            {
                Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
                Position = new Vector3f { X = 0, Y = 0, Z = 0 },
            },
        };

        Result result = Api.CreateActionSpace(_session, in info, ref _eyeGazeSpace);
        if (result != Result.Success)
        {
            Debug.LogWarning($"xrCreateActionSpace(eye gaze) failed: {result}");
            _eyeGazeSpace = default;
        }
    }

    private void SuggestEyeGazeBinding()
    {
        if (_eyeGazePoseAction.Handle == 0)
            return;

        try
        {
            SuggestForProfile(EyeGazeInteractionProfilePath,
            [
                new ActionSuggestedBinding
                {
                    Action = _eyeGazePoseAction,
                    Binding = StringToPathOrThrow(EyeGazePoseBindingPath),
                },
            ]);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR eye gaze binding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Samples the eye gaze pose in app space at the predicted display time. Called after actions sync.
    /// </summary>
    private void UpdateEyeGazePoseCache(long displayTime, int frameNumber)
    {
        bool valid = false;
        Matrix4x4 gaze = Matrix4x4.Identity;
        if (_eyeGazeSpace.Handle != 0 &&
            TryGetActivePoseState(_eyeGazePoseAction, default, out bool active) && active)
        {
            valid = TryLocateSpace(_eyeGazeSpace, displayTime, out gaze);
        }

        lock (_openXrPoseLock)
        {
            _openXrPredEyeGazeValid = valid ? 1 : 0;
            _openXrPredEyeGazeFrameNumber = frameNumber;
            if (valid)
                _openXrPredEyeGazeLocalPose = gaze;
        }
    }

    private void ClearEyeGazePoseCache()
    {
        lock (_openXrPoseLock)
        {
            _openXrPredEyeGazeValid = 0;
            _openXrPredEyeGazeFrameNumber = 0;
        }
    }

    private void DestroyEyeGaze()
    {
        if (_eyeGazeSpace.Handle != 0)
            Api.DestroySpace(_eyeGazeSpace);
        if (_eyeGazePoseAction.Handle != 0)
            Api.DestroyAction(_eyeGazePoseAction);

        _eyeGazeSpace = default;
        _eyeGazePoseAction = default;
        ClearEyeGazePoseCache();
    }

    /// <summary>
    /// Projects the current-frame gaze point into a view and returns its framebuffer UV, using the
    /// same NDC-to-framebuffer convention as the Advanced view record.
    /// </summary>
    /// <returns>False when no current gaze sample exists or the gaze point is behind the view.</returns>
    private bool TryResolveEyeGazeRenderTargetUv(uint viewIndex, out Vector2 renderTargetUv)
    {
        renderTargetUv = default;

        Matrix4x4 gaze;
        lock (_openXrPoseLock)
        {
            if (_openXrPredEyeGazeValid == 0 ||
                _openXrPredEyeGazeFrameNumber != Volatile.Read(ref _openXrPendingFrameNumber))
            {
                return false;
            }
            gaze = _openXrPredEyeGazeLocalPose;
        }

        if (!TryGetOpenXrViewPoseAndFov(viewIndex, OpenXrPoseTiming.Predicted, out Matrix4x4 viewPose, out var fov))
            return false;
        if (!Matrix4x4.Invert(viewPose, out Matrix4x4 inverseViewPose))
            return false;

        // The gaze looks down its local -Z axis, the same as an OpenXR view.
        Vector3 origin = gaze.Translation;
        Vector3 forward = new(-gaze.M31, -gaze.M32, -gaze.M33);
        if (forward.LengthSquared() < 1e-8f)
            return false;

        Vector3 fixation = origin + Vector3.Normalize(forward) * EyeGazeFixationDistanceMeters;
        Vector3 viewPoint = Vector3.Transform(fixation, inverseViewPose);
        if (viewPoint.Z > -1e-4f)
            return false;

        float tanX = viewPoint.X / -viewPoint.Z;
        float tanY = viewPoint.Y / -viewPoint.Z;
        float tanLeft = MathF.Tan(fov.Left);
        float tanRight = MathF.Tan(fov.Right);
        float tanUp = MathF.Tan(fov.Up);
        float tanDown = MathF.Tan(fov.Down);
        float width = tanRight - tanLeft;
        float height = tanUp - tanDown;
        if (width <= 0.0f || height <= 0.0f)
            return false;

        // Matches the off-center projection from CreateLocatedOpenXrProjection (clip Y up).
        Vector2 ndc = new(
            (2.0f * tanX - (tanRight + tanLeft)) / width,
            (2.0f * tanY - (tanUp + tanDown)) / height);
        Vector2 uv = ndc * 0.5f + new Vector2(0.5f);
        if (RenderClipSpacePolicy.FramebufferTextureYDirection(
                RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) == ERenderClipSpaceYDirection.YDown)
        {
            uv.Y = 1.0f - uv.Y;
        }

        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
            return false;

        renderTargetUv = Vector2.Clamp(uv, Vector2.Zero, Vector2.One);
        return true;
    }
}
