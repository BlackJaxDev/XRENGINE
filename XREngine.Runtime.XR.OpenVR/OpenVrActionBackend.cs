using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Input;
using BooleanAction = OpenVR.NET.Input.BooleanAction;
using HandSkeletonAction = OpenVR.NET.Input.HandSkeletonAction;
using HapticAction = OpenVR.NET.Input.HapticAction;
using OpenVRAction = OpenVR.NET.Input.Action;
using PoseAction = OpenVR.NET.Input.PoseAction;
using ScalarAction = OpenVR.NET.Input.ScalarAction;
using Vector2Action = OpenVR.NET.Input.Vector2Action;
using Vector3Action = OpenVR.NET.Input.Vector3Action;

namespace XREngine;

/// <summary>Reads typed OpenVR actions without exposing native action objects to input dispatch.</summary>
public static class OpenVrActionBackend
{
    private static readonly object EventSync = new();
    private static Action? _actionsChanged;

    public static bool HasActions => OpenVrRuntimeBackend.Actions.Count != 0;

    public static event Action? ActionsChanged
    {
        add
        {
            if (value is null)
                return;
            lock (EventSync)
            {
                if (_actionsChanged is null)
                    OpenVrRuntimeBackend.ActionsChanged += Forward;
                _actionsChanged += value;
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (EventSync)
            {
                _actionsChanged -= value;
                if (_actionsChanged is null)
                    OpenVrRuntimeBackend.ActionsChanged -= Forward;
            }
        }
    }

    private static void Forward(Dictionary<string, Dictionary<string, OpenVRAction>> actions)
        => _actionsChanged?.Invoke();

    public static bool HasAction(RuntimeVrActionValueType valueType, string category, string name)
        => valueType switch
        {
            RuntimeVrActionValueType.Boolean => TryGetAction(category, name, out BooleanAction? _),
            RuntimeVrActionValueType.Float => TryGetAction(category, name, out ScalarAction? _),
            RuntimeVrActionValueType.Vector2 => TryGetAction(category, name, out Vector2Action? _),
            RuntimeVrActionValueType.Vector3 => TryGetAction(category, name, out Vector3Action? _),
            RuntimeVrActionValueType.Pose => TryGetAction(category, name, out PoseAction? _),
            RuntimeVrActionValueType.Haptic => TryGetAction(category, name, out HapticAction? _),
            RuntimeVrActionValueType.HandSkeleton => TryGetAction(category, name, out HandSkeletonAction? _),
            _ => false,
        };

    public static bool TryReadBool(string category, string name, out bool value)
    {
        if (TryGetAction(category, name, out BooleanAction? action))
        {
            action.Update();
            value = action.Value;
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryReadFloat(string category, string name, out float value)
    {
        if (TryGetAction(category, name, out ScalarAction? action))
        {
            action.Update();
            value = action.Value;
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryReadVector2(string category, string name, out Vector2 value)
    {
        if (TryGetAction(category, name, out Vector2Action? action))
        {
            action.Update();
            value = action.Value;
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryReadVector3(string category, string name, out Vector3 value)
    {
        if (TryGetAction(category, name, out Vector3Action? action))
        {
            action.Update();
            value = action.Value;
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryReadPose(string category, string name, out RuntimeVrPoseState pose)
    {
        if (TryGetAction(category, name, out PoseAction? action) &&
            action.FetchDataForPrediction(0f) is { } data)
        {
            pose = new RuntimeVrPoseState(
                data.DeviceToAbsoluteTrackingMatrix,
                data.Position,
                data.Rotation,
                data.Velocity,
                data.AngularVelocity,
                isActive: true,
                isValid: true);
            return true;
        }
        pose = default;
        return false;
    }

    public static bool TryReadSkeletonSummary(string category, string name, out RuntimeVrSkeletonSummary result)
    {
        if (TryGetAction(category, name, out HandSkeletonAction? action) &&
            action.GetSummary(Valve.VR.EVRSummaryType.FromDevice) is { } summary)
        {
            action.Update();
            result = new RuntimeVrSkeletonSummary(
                summary.ThumbCurl, summary.IndexCurl, summary.MiddleCurl,
                summary.RingCurl, summary.PinkyCurl, summary.ThumbIndexSplay,
                summary.IndexMiddleSplay, summary.MiddleRingSplay, summary.RingPinkySplay,
                hasRealHandJoints: true,
                isActive: true);
            return true;
        }
        result = default;
        return false;
    }

    public static bool Vibrate(string category, string name, double duration, double frequency, double amplitude, double delay)
        => TryGetAction(category, name, out HapticAction? action) &&
            action.TriggerVibration(duration, frequency, amplitude, delay);

    private static bool TryGetAction<TAction>(string category, string name, [NotNullWhen(true)] out TAction? action)
        where TAction : OpenVRAction
    {
        action = null;
        if (!OpenVrRuntimeBackend.Actions.TryGetValue(category, out Dictionary<string, OpenVRAction>? actions) ||
            !actions.TryGetValue(name, out OpenVRAction? raw) ||
            raw is not TAction typed)
            return false;

        action = typed;
        return true;
    }
}
