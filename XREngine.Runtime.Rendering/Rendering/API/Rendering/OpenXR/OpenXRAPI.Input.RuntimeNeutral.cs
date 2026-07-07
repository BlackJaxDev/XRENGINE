using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.EXT;
using Silk.NET.OpenXR.Extensions.HTCX;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Input;
using Debug = XREngine.Debug;
using XrAction = Silk.NET.OpenXR.Action;
using XrPath = System.UInt64;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private const string OpenXrHandTrackingExtensionName = "XR_EXT_hand_tracking";
    private const int OpenXrHandJointCount = 26;

    private XrAction _handAimPoseAction;
    private Space _leftHandAimSpace;
    private Space _rightHandAimSpace;
    private Matrix4x4 _openXrPredLeftControllerAimLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrPredRightControllerAimLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrLateLeftControllerAimLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrLateRightControllerAimLocalPose = Matrix4x4.Identity;
    private int _openXrPredLeftControllerAimValid;
    private int _openXrPredRightControllerAimValid;
    private int _openXrLateLeftControllerAimValid;
    private int _openXrLateRightControllerAimValid;

    private XrAction _hapticAction;
    private readonly Dictionary<string, OpenXrRuntimeInputAction> _runtimeInputActions = new(StringComparer.Ordinal);
    private readonly List<OpenXrRuntimeInputAction> _runtimeInputActionList = [];

    private ExtHandTracking? _handTracking;
    private HandTrackerEXT _leftHandTracker;
    private HandTrackerEXT _rightHandTracker;
    private readonly HandJointLocationEXT[] _leftHandJointLocations = new HandJointLocationEXT[OpenXrHandJointCount];
    private readonly HandJointLocationEXT[] _rightHandJointLocations = new HandJointLocationEXT[OpenXrHandJointCount];
    private readonly RuntimeVrHandJointState[] _leftHandJointStates = new RuntimeVrHandJointState[OpenXrHandJointCount];
    private readonly RuntimeVrHandJointState[] _rightHandJointStates = new RuntimeVrHandJointState[OpenXrHandJointCount];
    private int _leftHandJointsActive;
    private int _rightHandJointsActive;
    private int _handTrackingUnavailableLogged;

    private HtcxViveTrackerInteraction? _viveTrackerInteraction;
    private readonly Dictionary<string, string> _viveTrackerPersistentToRolePaths = new(StringComparer.Ordinal);
    private int _viveTrackerExtensionUnavailableLogged;

    public bool IsInputActionKnown(string category, string name, RuntimeVrActionValueType valueType)
    {
        if (valueType == RuntimeVrActionValueType.Haptic)
            return _hapticAction.Handle != 0;

        return _runtimeInputActions.TryGetValue(MakeRuntimeInputKey(category, name), out OpenXrRuntimeInputAction? action) &&
               action.ValueType == valueType;
    }

    public bool TryGetBooleanActionState(string category, string name, out bool value, out bool active)
    {
        if (TryGetRuntimeInputAction(category, name, RuntimeVrActionValueType.Boolean, out OpenXrRuntimeInputAction? action))
        {
            value = action.BoolValue;
            active = action.Active;
            return true;
        }

        value = false;
        active = false;
        return false;
    }

    public bool TryGetFloatActionState(string category, string name, out float value, out bool active)
    {
        if (TryGetRuntimeInputAction(category, name, RuntimeVrActionValueType.Float, out OpenXrRuntimeInputAction? action))
        {
            value = action.FloatValue;
            active = action.Active;
            return true;
        }

        value = 0.0f;
        active = false;
        return false;
    }

    public bool TryGetVector2ActionState(string category, string name, out Vector2 value, out bool active)
    {
        if (TryGetRuntimeInputAction(category, name, RuntimeVrActionValueType.Vector2, out OpenXrRuntimeInputAction? action))
        {
            value = action.Vector2Value;
            active = action.Active;
            return true;
        }

        value = Vector2.Zero;
        active = false;
        return false;
    }

    public bool TryGetVector3ActionState(string category, string name, out Vector3 value, out bool active)
    {
        value = Vector3.Zero;
        active = false;
        return false;
    }

    public bool TryGetControllerPoseState(bool leftHand, RuntimeVrPoseKind poseKind, OpenXrPoseTiming timing, out RuntimeVrPoseState pose)
    {
        bool valid;
        Matrix4x4 localPose;
        lock (_openXrPoseLock)
        {
            if (poseKind == RuntimeVrPoseKind.Aim)
            {
                if (timing == OpenXrPoseTiming.Late)
                {
                    localPose = leftHand ? _openXrLateLeftControllerAimLocalPose : _openXrLateRightControllerAimLocalPose;
                    valid = (leftHand ? _openXrLateLeftControllerAimValid : _openXrLateRightControllerAimValid) != 0;
                }
                else
                {
                    localPose = leftHand ? _openXrPredLeftControllerAimLocalPose : _openXrPredRightControllerAimLocalPose;
                    valid = (leftHand ? _openXrPredLeftControllerAimValid : _openXrPredRightControllerAimValid) != 0;
                }
            }
            else
            {
                if (timing == OpenXrPoseTiming.Late)
                {
                    localPose = leftHand ? _openXrLateLeftControllerLocalPose : _openXrLateRightControllerLocalPose;
                    valid = (leftHand ? _openXrLateLeftControllerValid : _openXrLateRightControllerValid) != 0;
                }
                else
                {
                    localPose = leftHand ? _openXrPredLeftControllerLocalPose : _openXrPredRightControllerLocalPose;
                    valid = (leftHand ? _openXrPredLeftControllerValid : _openXrPredRightControllerValid) != 0;
                }
            }
        }

        if (!valid)
        {
            pose = default;
            return false;
        }

        Matrix4x4.Decompose(localPose, out _, out Quaternion rotation, out Vector3 position);
        pose = new RuntimeVrPoseState(localPose, position, rotation, Vector3.Zero, Vector3.Zero, isActive: true, isValid: true);
        return true;
    }

    public bool TryGetHandJointState(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state)
    {
        int index = (int)joint;
        RuntimeVrHandJointState[] source = leftHand ? _leftHandJointStates : _rightHandJointStates;
        if ((uint)index >= (uint)source.Length)
        {
            state = default;
            return false;
        }

        bool active = (leftHand ? _leftHandJointsActive : _rightHandJointsActive) != 0;
        state = source[index];
        return active && state.Tracked;
    }

    public bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary)
    {
        bool active = (leftHand ? _leftHandJointsActive : _rightHandJointsActive) != 0;
        if (active)
        {
            RuntimeVrHandJointState[] joints = leftHand ? _leftHandJointStates : _rightHandJointStates;
            summary = new RuntimeVrSkeletonSummary(
                EstimateFingerCurl(joints, RuntimeVrHandJoint.ThumbMetacarpal, RuntimeVrHandJoint.ThumbTip),
                EstimateFingerCurl(joints, RuntimeVrHandJoint.IndexMetacarpal, RuntimeVrHandJoint.IndexTip),
                EstimateFingerCurl(joints, RuntimeVrHandJoint.MiddleMetacarpal, RuntimeVrHandJoint.MiddleTip),
                EstimateFingerCurl(joints, RuntimeVrHandJoint.RingMetacarpal, RuntimeVrHandJoint.RingTip),
                EstimateFingerCurl(joints, RuntimeVrHandJoint.LittleMetacarpal, RuntimeVrHandJoint.LittleTip),
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                hasRealHandJoints: true,
                isActive: true);
            return true;
        }

        string grabName = leftHand ? "GrabLeft" : "GrabRight";
        if (TryGetFloatActionState("Global", grabName, out float grab, out bool grabActive) && grabActive)
        {
            float curl = Math.Clamp(grab, 0.0f, 1.0f);
            summary = new RuntimeVrSkeletonSummary(
                curl,
                curl,
                curl,
                curl,
                curl,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                hasRealHandJoints: false,
                isActive: true);
            return true;
        }

        summary = default;
        return false;
    }

    public bool ApplyHapticAction(string category, string name, double duration, double frequency, double amplitude, double delay)
    {
        if (_hapticAction.Handle == 0 || _session.Handle == 0)
            return false;

        if (delay > 0)
            Debug.Out("OpenXR haptic delay was requested, but core OpenXR haptics do not support delayed playback; applying immediately.");

        XrPath subactionPath = ResolveHapticSubactionPath(name);
        var hapticInfo = new HapticActionInfo
        {
            Type = StructureType.HapticActionInfo,
            Action = _hapticAction,
            SubactionPath = subactionPath,
        };

        long durationNs = duration <= 0
            ? 0
            : (long)Math.Min(duration * 1_000_000_000.0, long.MaxValue);
        var vibration = new HapticVibration
        {
            Type = StructureType.HapticVibration,
            Duration = durationNs,
            Frequency = (float)frequency,
            Amplitude = (float)Math.Clamp(amplitude, 0.0, 1.0),
        };

        Result result = Api.ApplyHapticFeedback(_session, in hapticInfo, (HapticBaseHeader*)&vibration);
        if (result != Result.Success)
        {
            Debug.Out($"OpenXR: xrApplyHapticFeedback({category}/{name}) => {result}");
            return false;
        }

        return true;
    }

    public bool StopHapticAction(string category, string name)
    {
        if (_hapticAction.Handle == 0 || _session.Handle == 0)
            return false;

        var hapticInfo = new HapticActionInfo
        {
            Type = StructureType.HapticActionInfo,
            Action = _hapticAction,
            SubactionPath = ResolveHapticSubactionPath(name),
        };

        Result result = Api.StopHapticFeedback(_session, in hapticInfo);
        if (result != Result.Success)
        {
            Debug.Out($"OpenXR: xrStopHapticFeedback({category}/{name}) => {result}");
            return false;
        }

        return true;
    }

    private void CreateRuntimeNeutralInputActions()
    {
        _runtimeInputActions.Clear();
        _runtimeInputActionList.Clear();

        CreateRuntimeInputAction("Global", "Interact", RuntimeVrActionValueType.Boolean, ActionType.BooleanInput, _rightHandPath, "Interact");
        CreateRuntimeInputAction("Global", "Jump", RuntimeVrActionValueType.Boolean, ActionType.BooleanInput, _rightHandPath, "Jump");
        CreateRuntimeInputAction("Global", "ToggleQuickMenu", RuntimeVrActionValueType.Boolean, ActionType.BooleanInput, _leftHandPath, "Quick Menu");
        CreateRuntimeInputAction("Global", "ToggleMute", RuntimeVrActionValueType.Boolean, ActionType.BooleanInput, _leftHandPath, "Mute");
        CreateRuntimeInputAction("Global", "GrabLeft", RuntimeVrActionValueType.Float, ActionType.FloatInput, _leftHandPath, "Grab Left");
        CreateRuntimeInputAction("Global", "GrabRight", RuntimeVrActionValueType.Float, ActionType.FloatInput, _rightHandPath, "Grab Right");
        CreateRuntimeInputAction("Global", "Locomote", RuntimeVrActionValueType.Vector2, ActionType.Vector2fInput, _leftHandPath, "Locomote");
        CreateRuntimeInputAction("Global", "Turn", RuntimeVrActionValueType.Vector2, ActionType.Vector2fInput, _rightHandPath, "Turn");
        CreateRuntimeHapticAction();
        CreateAimPoseAction();
    }

    private void CreateRuntimeInputAction(
        string category,
        string name,
        RuntimeVrActionValueType valueType,
        ActionType actionType,
        XrPath subactionPath,
        string localizedName)
    {
        XrPath* subactionPaths = stackalloc XrPath[1] { subactionPath };
        var actionInfo = new ActionCreateInfo
        {
            Type = StructureType.ActionCreateInfo,
            ActionType = actionType,
            CountSubactionPaths = 1,
            SubactionPaths = subactionPaths,
        };

        WriteUtf8Z(actionInfo.ActionName, 64, MakeOpenXrActionName(category, name));
        WriteUtf8Z(actionInfo.LocalizedActionName, 128, localizedName);

        XrAction action = default;
        Result result = Api.CreateAction(_inputActionSet, in actionInfo, ref action);
        if (result != Result.Success)
        {
            Debug.LogWarning($"xrCreateAction({category}/{name}) failed: {result}");
            return;
        }

        var runtimeAction = new OpenXrRuntimeInputAction(category, name, valueType, action, subactionPath);
        _runtimeInputActions[MakeRuntimeInputKey(category, name)] = runtimeAction;
        _runtimeInputActionList.Add(runtimeAction);
    }

    private void CreateRuntimeHapticAction()
    {
        XrPath* handSubactionPaths = stackalloc XrPath[2] { _leftHandPath, _rightHandPath };
        var actionInfo = new ActionCreateInfo
        {
            Type = StructureType.ActionCreateInfo,
            ActionType = ActionType.VibrationOutput,
            CountSubactionPaths = 2,
            SubactionPaths = handSubactionPaths,
        };

        WriteUtf8Z(actionInfo.ActionName, 64, "global_haptics");
        WriteUtf8Z(actionInfo.LocalizedActionName, 128, "Haptics");

        Result result = Api.CreateAction(_inputActionSet, in actionInfo, ref _hapticAction);
        if (result != Result.Success)
            Debug.LogWarning($"xrCreateAction(global_haptics) failed: {result}");
    }

    private void CreateAimPoseAction()
    {
        XrPath* handSubactionPaths = stackalloc XrPath[2] { _leftHandPath, _rightHandPath };
        var handPoseInfo = new ActionCreateInfo
        {
            Type = StructureType.ActionCreateInfo,
            ActionType = ActionType.PoseInput,
            CountSubactionPaths = 2,
            SubactionPaths = handSubactionPaths,
        };

        WriteUtf8Z(handPoseInfo.ActionName, 64, "hand_aim_pose");
        WriteUtf8Z(handPoseInfo.LocalizedActionName, 128, "Hand Aim Pose");

        Result result = Api.CreateAction(_inputActionSet, in handPoseInfo, ref _handAimPoseAction);
        if (result != Result.Success)
            Debug.LogWarning($"xrCreateAction(hand_aim_pose) failed: {result}");
    }

    private void CreateRuntimeNeutralActionSpaces()
    {
        CreateAimPoseSpaces();
        CreateHandTrackers();
    }

    private void CreateAimPoseSpaces()
    {
        if (_handAimPoseAction.Handle == 0)
            return;

        var identity = new Posef
        {
            Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
            Position = new Vector3f { X = 0, Y = 0, Z = 0 }
        };

        var leftInfo = new ActionSpaceCreateInfo
        {
            Type = StructureType.ActionSpaceCreateInfo,
            Action = _handAimPoseAction,
            SubactionPath = _leftHandPath,
            PoseInActionSpace = identity,
        };

        var rightInfo = new ActionSpaceCreateInfo
        {
            Type = StructureType.ActionSpaceCreateInfo,
            Action = _handAimPoseAction,
            SubactionPath = _rightHandPath,
            PoseInActionSpace = identity,
        };

        Result leftRes = Api.CreateActionSpace(_session, in leftInfo, ref _leftHandAimSpace);
        if (leftRes != Result.Success)
            Debug.LogWarning($"xrCreateActionSpace(left aim hand) failed: {leftRes}");

        Result rightRes = Api.CreateActionSpace(_session, in rightInfo, ref _rightHandAimSpace);
        if (rightRes != Result.Success)
            Debug.LogWarning($"xrCreateActionSpace(right aim hand) failed: {rightRes}");
    }

    private void SuggestRuntimeNeutralBindings()
    {
        if (_handAimPoseAction.Handle == 0 && _runtimeInputActionList.Count == 0 && _hapticAction.Handle == 0)
            return;

        SuggestRuntimeBindingsForProfile(
            "/interaction_profiles/valve/index_controller",
            (GetAction("Global", "Locomote"), "/user/hand/left/input/thumbstick"),
            (GetAction("Global", "Turn"), "/user/hand/right/input/thumbstick"),
            (GetAction("Global", "GrabLeft"), "/user/hand/left/input/squeeze/value"),
            (GetAction("Global", "GrabRight"), "/user/hand/right/input/squeeze/value"),
            (GetAction("Global", "Jump"), "/user/hand/right/input/a/click"),
            (GetAction("Global", "ToggleQuickMenu"), "/user/hand/left/input/system/click"),
            (GetAction("Global", "ToggleMute"), "/user/hand/left/input/b/click"),
            (_handAimPoseAction, "/user/hand/left/input/aim/pose"),
            (_handAimPoseAction, "/user/hand/right/input/aim/pose"),
            (_hapticAction, "/user/hand/left/output/haptic"),
            (_hapticAction, "/user/hand/right/output/haptic"));

        SuggestRuntimeBindingsForProfile(
            "/interaction_profiles/htc/vive_controller",
            (GetAction("Global", "Locomote"), "/user/hand/left/input/trackpad"),
            (GetAction("Global", "Turn"), "/user/hand/right/input/trackpad"),
            (GetAction("Global", "GrabLeft"), "/user/hand/left/input/trigger/value"),
            (GetAction("Global", "GrabRight"), "/user/hand/right/input/trigger/value"),
            (GetAction("Global", "Jump"), "/user/hand/right/input/trackpad/click"),
            (GetAction("Global", "ToggleQuickMenu"), "/user/hand/left/input/menu/click"),
            (GetAction("Global", "ToggleMute"), "/user/hand/left/input/grip/click"),
            (_handAimPoseAction, "/user/hand/left/input/aim/pose"),
            (_handAimPoseAction, "/user/hand/right/input/aim/pose"),
            (_hapticAction, "/user/hand/left/output/haptic"),
            (_hapticAction, "/user/hand/right/output/haptic"));

        SuggestRuntimeBindingsForProfile(
            "/interaction_profiles/khr/simple_controller",
            (GetAction("Global", "GrabLeft"), "/user/hand/left/input/select/click"),
            (GetAction("Global", "GrabRight"), "/user/hand/right/input/select/click"),
            (GetAction("Global", "Jump"), "/user/hand/right/input/menu/click"),
            (_handAimPoseAction, "/user/hand/left/input/aim/pose"),
            (_handAimPoseAction, "/user/hand/right/input/aim/pose"),
            (_hapticAction, "/user/hand/left/output/haptic"),
            (_hapticAction, "/user/hand/right/output/haptic"));

        SuggestRuntimeBindingsForProfile(
            "/interaction_profiles/oculus/touch_controller",
            (GetAction("Global", "Locomote"), "/user/hand/left/input/thumbstick"),
            (GetAction("Global", "Turn"), "/user/hand/right/input/thumbstick"),
            (GetAction("Global", "GrabLeft"), "/user/hand/left/input/squeeze/value"),
            (GetAction("Global", "GrabRight"), "/user/hand/right/input/squeeze/value"),
            (GetAction("Global", "Jump"), "/user/hand/right/input/a/click"),
            (GetAction("Global", "ToggleQuickMenu"), "/user/hand/left/input/menu/click"),
            (GetAction("Global", "ToggleMute"), "/user/hand/left/input/x/click"),
            (_handAimPoseAction, "/user/hand/left/input/aim/pose"),
            (_handAimPoseAction, "/user/hand/right/input/aim/pose"),
            (_hapticAction, "/user/hand/left/output/haptic"),
            (_hapticAction, "/user/hand/right/output/haptic"));

        SuggestRuntimeBindingsForProfile(
            "/interaction_profiles/microsoft/motion_controller",
            (GetAction("Global", "Locomote"), "/user/hand/left/input/thumbstick"),
            (GetAction("Global", "Turn"), "/user/hand/right/input/thumbstick"),
            (GetAction("Global", "GrabLeft"), "/user/hand/left/input/squeeze/value"),
            (GetAction("Global", "GrabRight"), "/user/hand/right/input/squeeze/value"),
            (GetAction("Global", "Jump"), "/user/hand/right/input/trackpad/click"),
            (GetAction("Global", "ToggleQuickMenu"), "/user/hand/left/input/menu/click"),
            (_handAimPoseAction, "/user/hand/left/input/aim/pose"),
            (_handAimPoseAction, "/user/hand/right/input/aim/pose"),
            (_hapticAction, "/user/hand/left/output/haptic"),
            (_hapticAction, "/user/hand/right/output/haptic"));
    }

    private void SuggestRuntimeBindingsForProfile(string profilePath, params (XrAction Action, string BindingPath)[] requestedBindings)
    {
        if (requestedBindings.Length == 0)
            return;

        var bindings = new List<ActionSuggestedBinding>(requestedBindings.Length);
        for (int i = 0; i < requestedBindings.Length; i++)
        {
            XrAction action = requestedBindings[i].Action;
            if (action.Handle == 0)
                continue;

            try
            {
                bindings.Add(new ActionSuggestedBinding
                {
                    Action = action,
                    Binding = StringToPathOrThrow(requestedBindings[i].BindingPath),
                });
            }
            catch (Exception ex)
            {
                Debug.Out($"OpenXR: skipped binding {profilePath} {requestedBindings[i].BindingPath}: {ex.Message}");
            }
        }

        if (bindings.Count > 0)
            SuggestForProfile(profilePath, bindings.ToArray());
    }

    private void UpdateRuntimeNeutralInputStateCaches(long displayTime, OpenXrPoseTiming timing)
    {
        UpdateRuntimeActionStates();
        UpdateAimPoseCaches(displayTime, timing);
        UpdateHandTrackingCaches(displayTime);
    }

    private void UpdateRuntimeActionStates()
    {
        for (int i = 0; i < _runtimeInputActionList.Count; i++)
        {
            OpenXrRuntimeInputAction action = _runtimeInputActionList[i];
            var getInfo = new ActionStateGetInfo
            {
                Type = StructureType.ActionStateGetInfo,
                Action = action.Action,
                SubactionPath = action.SubactionPath,
            };

            Result result;
            switch (action.ValueType)
            {
                case RuntimeVrActionValueType.Boolean:
                    {
                        var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
                        result = Api.GetActionStateBoolean(_session, in getInfo, ref state);
                        if (result == Result.Success)
                        {
                            action.BoolValue = state.CurrentState != 0;
                            action.Active = state.IsActive != 0;
                        }
                    }
                    break;
                case RuntimeVrActionValueType.Float:
                    {
                        var state = new ActionStateFloat { Type = StructureType.ActionStateFloat };
                        result = Api.GetActionStateFloat(_session, in getInfo, ref state);
                        if (result == Result.Success)
                        {
                            action.FloatValue = state.CurrentState;
                            action.Active = state.IsActive != 0;
                        }
                    }
                    break;
                case RuntimeVrActionValueType.Vector2:
                    {
                        var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };
                        result = Api.GetActionStateVector2(_session, in getInfo, ref state);
                        if (result == Result.Success)
                        {
                            action.Vector2Value = new Vector2(state.CurrentState.X, state.CurrentState.Y);
                            action.Active = state.IsActive != 0;
                        }
                    }
                    break;
                default:
                    result = Result.Success;
                    break;
            }

            if (result != Result.Success)
            {
                action.Active = false;
                if (!action.ErrorLogged)
                {
                    action.ErrorLogged = true;
                    Debug.Out($"OpenXR: action state query failed for {action.Category}/{action.Name}: {result}");
                }
            }
            else if (!action.Active && !action.InactiveLogged)
            {
                action.InactiveLogged = true;
                Debug.Out($"OpenXR: action {action.Category}/{action.Name} is inactive. Check runtime bindings for the active interaction profile.");
            }
            else if (action.Active)
            {
                action.InactiveLogged = false;
            }
        }
    }

    private void UpdateAimPoseCaches(long displayTime, OpenXrPoseTiming timing)
    {
        if (_handAimPoseAction.Handle == 0)
            return;

        bool leftActive = false;
        bool rightActive = false;
        _ = TryGetActivePoseState(_handAimPoseAction, _leftHandPath, out leftActive);
        _ = TryGetActivePoseState(_handAimPoseAction, _rightHandPath, out rightActive);

        Matrix4x4 leftLocal = Matrix4x4.Identity;
        Matrix4x4 rightLocal = Matrix4x4.Identity;
        bool leftValid = leftActive && TryLocateSpace(_leftHandAimSpace, displayTime, out leftLocal);
        bool rightValid = rightActive && TryLocateSpace(_rightHandAimSpace, displayTime, out rightLocal);

        lock (_openXrPoseLock)
        {
            if (timing == OpenXrPoseTiming.Late)
            {
                _openXrLateLeftControllerAimValid = leftValid ? 1 : 0;
                _openXrLateRightControllerAimValid = rightValid ? 1 : 0;
                if (leftValid)
                    _openXrLateLeftControllerAimLocalPose = leftLocal;
                if (rightValid)
                    _openXrLateRightControllerAimLocalPose = rightLocal;
            }
            else
            {
                _openXrPredLeftControllerAimValid = leftValid ? 1 : 0;
                _openXrPredRightControllerAimValid = rightValid ? 1 : 0;
                if (leftValid)
                    _openXrPredLeftControllerAimLocalPose = leftLocal;
                if (rightValid)
                    _openXrPredRightControllerAimLocalPose = rightLocal;
            }
        }
    }

    private void CreateHandTrackers()
    {
        if (!IsInstanceExtensionEnabled(OpenXrHandTrackingExtensionName))
        {
            LogHandTrackingUnavailable(DescribeInstanceExtensionState(OpenXrHandTrackingExtensionName));
            return;
        }

        try
        {
            if (!Api.TryGetInstanceExtension<ExtHandTracking>(string.Empty, _instance, out _handTracking) || _handTracking is null)
            {
                LogHandTrackingUnavailable("extension advertised but delegate loading failed");
                return;
            }
        }
        catch (Exception ex)
        {
            LogHandTrackingUnavailable($"extension delegate load failed: {ex.Message}");
            return;
        }

        CreateHandTracker(true, ref _leftHandTracker);
        CreateHandTracker(false, ref _rightHandTracker);
    }

    private void CreateHandTracker(bool leftHand, ref HandTrackerEXT tracker)
    {
        if (_handTracking is null)
            return;

        var createInfo = new HandTrackerCreateInfoEXT
        {
            Type = StructureType.HandTrackerCreateInfoExt,
            Hand = leftHand ? HandEXT.LeftExt : HandEXT.RightExt,
            HandJointSet = HandJointSetEXT.DefaultExt,
        };

        Result result = _handTracking.CreateHandTracker(_session, in createInfo, ref tracker);
        if (result != Result.Success)
            Debug.LogWarning($"OpenXR: xrCreateHandTrackerEXT({(leftHand ? "left" : "right")}) failed: {result}");
    }

    private void UpdateHandTrackingCaches(long displayTime)
    {
        if (_handTracking is null)
            return;

        UpdateHandTrackingCache(true, _leftHandTracker, _leftHandJointLocations, _leftHandJointStates, ref _leftHandJointsActive, displayTime);
        UpdateHandTrackingCache(false, _rightHandTracker, _rightHandJointLocations, _rightHandJointStates, ref _rightHandJointsActive, displayTime);
    }

    private void UpdateHandTrackingCache(
        bool leftHand,
        HandTrackerEXT tracker,
        HandJointLocationEXT[] jointLocations,
        RuntimeVrHandJointState[] jointStates,
        ref int activeField,
        long displayTime)
    {
        if (tracker.Handle == 0 || _handTracking is null)
            return;

        fixed (HandJointLocationEXT* jointsPtr = jointLocations)
        {
            var locateInfo = new HandJointsLocateInfoEXT
            {
                Type = StructureType.HandJointsLocateInfoExt,
                BaseSpace = _appSpace,
                Time = displayTime,
            };

            var locations = new HandJointLocationsEXT
            {
                Type = StructureType.HandJointLocationsExt,
                JointCount = OpenXrHandJointCount,
                JointLocations = jointsPtr,
            };

            Result result = _handTracking.LocateHandJoints(tracker, in locateInfo, ref locations);
            if (result != Result.Success)
            {
                activeField = 0;
                return;
            }

            activeField = locations.IsActive != 0 ? 1 : 0;
            if (locations.IsActive == 0)
                return;
        }

        for (int i = 0; i < OpenXrHandJointCount; i++)
        {
            HandJointLocationEXT source = jointLocations[i];
            bool positionValid = (source.LocationFlags & SpaceLocationFlags.PositionValidBit) != 0;
            bool rotationValid = (source.LocationFlags & SpaceLocationFlags.OrientationValidBit) != 0;
            bool tracked = (source.LocationFlags & (SpaceLocationFlags.PositionTrackedBit | SpaceLocationFlags.OrientationTrackedBit)) != 0;
            Posef pose = source.Pose;
            jointStates[i] = new RuntimeVrHandJointState(
                new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
                Quaternion.Normalize(new Quaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W)),
                source.Radius,
                positionValid,
                rotationValid,
                tracked);
        }
    }

    private void DestroyRuntimeNeutralInput()
    {
        DestroyHandTrackers();

        if (_leftHandAimSpace.Handle != 0)
            Api.DestroySpace(_leftHandAimSpace);
        if (_rightHandAimSpace.Handle != 0)
            Api.DestroySpace(_rightHandAimSpace);
        if (_handAimPoseAction.Handle != 0)
            Api.DestroyAction(_handAimPoseAction);

        for (int i = 0; i < _runtimeInputActionList.Count; i++)
        {
            XrAction action = _runtimeInputActionList[i].Action;
            if (action.Handle != 0)
                Api.DestroyAction(action);
        }

        if (_hapticAction.Handle != 0)
            Api.DestroyAction(_hapticAction);

        _leftHandAimSpace = default;
        _rightHandAimSpace = default;
        _handAimPoseAction = default;
        _hapticAction = default;
        _runtimeInputActions.Clear();
        _runtimeInputActionList.Clear();
        _openXrPredLeftControllerAimValid = 0;
        _openXrPredRightControllerAimValid = 0;
        _openXrLateLeftControllerAimValid = 0;
        _openXrLateRightControllerAimValid = 0;
    }

    private void DestroyHandTrackers()
    {
        try
        {
            if (_handTracking is not null)
            {
                if (_leftHandTracker.Handle != 0)
                    _handTracking.DestroyHandTracker(_leftHandTracker);
                if (_rightHandTracker.Handle != 0)
                    _handTracking.DestroyHandTracker(_rightHandTracker);
            }
        }
        catch
        {
            // Best-effort teardown.
        }
        finally
        {
            _leftHandTracker = default;
            _rightHandTracker = default;
            _handTracking = null;
            _leftHandJointsActive = 0;
            _rightHandJointsActive = 0;
        }
    }

    private void InitializeViveTrackerExtension()
    {
        _viveTrackerInteraction = null;
        if (!IsInstanceExtensionEnabled(HtcxViveTrackerInteraction.ExtensionName))
        {
            LogViveTrackerExtensionUnavailable(DescribeInstanceExtensionState(HtcxViveTrackerInteraction.ExtensionName));
            return;
        }

        try
        {
            if (!Api.TryGetInstanceExtension<HtcxViveTrackerInteraction>(string.Empty, _instance, out _viveTrackerInteraction) ||
                _viveTrackerInteraction is null)
            {
                LogViveTrackerExtensionUnavailable("extension advertised but delegate loading failed");
                return;
            }
        }
        catch (Exception ex)
        {
            LogViveTrackerExtensionUnavailable($"extension delegate load failed: {ex.Message}");
            return;
        }

        EnumerateViveTrackerPaths();
    }

    private void EnumerateViveTrackerPaths()
    {
        if (_viveTrackerInteraction is null)
            return;

        uint count = 0;
        Result countResult = _viveTrackerInteraction.EnumerateViveTrackerPathsHtcx(_instance, 0, ref count, null);
        if (countResult != Result.Success || count == 0)
            return;

        var paths = new ViveTrackerPathsHTCX[count];
        for (int i = 0; i < paths.Length; i++)
            paths[i].Type = StructureType.ViveTrackerPathsHtcx;

        fixed (ViveTrackerPathsHTCX* pathsPtr = paths)
        {
            Result result = _viveTrackerInteraction.EnumerateViveTrackerPathsHtcx(_instance, count, ref count, pathsPtr);
            if (result != Result.Success)
            {
                Debug.Out($"OpenXR: xrEnumerateViveTrackerPathsHTCX failed: {result}");
                return;
            }
        }

        for (int i = 0; i < count && i < paths.Length; i++)
            AddViveTrackerPaths(paths[i]);
    }

    private void HandleViveTrackerConnectedEvent(EventDataViveTrackerConnectedHTCX* connected)
    {
        if (connected is null)
            return;

        ViveTrackerPathsHTCX* paths = connected->Paths;
        if (paths is not null)
            AddViveTrackerPaths(*paths);
        Debug.Out("OpenXR: Vive tracker connected or role changed. Ensure tracker body roles are assigned in SteamVR when expected poses are missing.");
    }

    private void AddViveTrackerPaths(ViveTrackerPathsHTCX paths)
    {
        string? persistentPath = PathToString(paths.PersistentPath);
        string? rolePath = PathToString(paths.RolePath);

        lock (_openXrPoseLock)
        {
            RegisterViveTrackerLocked(
                ResolveCanonicalTrackerUserPath(rolePath, persistentPath),
                persistentPath,
                rolePath,
                poseAvailable: false,
                runtimeReported: true);
        }

        if (!string.IsNullOrWhiteSpace(persistentPath) && !string.IsNullOrWhiteSpace(rolePath))
            _viveTrackerPersistentToRolePaths[persistentPath] = rolePath;

        AddTrackerSubactionPath(rolePath);
        AddTrackerSubactionPath(persistentPath);
    }

    private string ResolveCanonicalTrackerUserPath(string userPath)
        => _viveTrackerPersistentToRolePaths.TryGetValue(userPath, out string? rolePath) &&
           !string.IsNullOrWhiteSpace(rolePath)
            ? rolePath
            : userPath;

    private static string? ResolveCanonicalTrackerUserPath(string? rolePath, string? persistentPath)
        => !string.IsNullOrWhiteSpace(rolePath)
            ? rolePath
            : persistentPath;

    private void MarkTrackerPoseAvailableLocked(string userPath, string canonicalPath)
    {
        if (!string.IsNullOrWhiteSpace(canonicalPath))
        {
            string? persistentPath = string.Equals(canonicalPath, userPath, StringComparison.Ordinal)
                ? null
                : userPath;
            string? rolePath = GetViveTrackerRoleName(canonicalPath) is not null
                ? canonicalPath
                : null;
            RegisterViveTrackerLocked(canonicalPath, persistentPath, rolePath, poseAvailable: true, runtimeReported: false);
            return;
        }

        RegisterViveTrackerLocked(userPath, null, null, poseAvailable: true, runtimeReported: false);
    }

    private void RegisterViveTrackerLocked(
        string? userPath,
        string? persistentPath,
        string? rolePath,
        bool poseAvailable,
        bool runtimeReported)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            return;

        _openXrKnownTrackerPaths.Add(userPath);

        _openXrKnownTrackers.TryGetValue(userPath, out RuntimeVrTrackerInfo previous);
        string? effectivePersistentPath = !string.IsNullOrWhiteSpace(persistentPath)
            ? persistentPath
            : previous.PersistentPath;
        string? effectiveRolePath = !string.IsNullOrWhiteSpace(rolePath)
            ? rolePath
            : previous.RolePath;

        _openXrKnownTrackers[userPath] = new RuntimeVrTrackerInfo(
            userPath,
            effectivePersistentPath,
            effectiveRolePath,
            GetViveTrackerRoleName(effectiveRolePath ?? userPath),
            previous.PoseAvailable || poseAvailable,
            previous.RuntimeReported || runtimeReported);
    }

    private static string? GetViveTrackerRoleName(string? userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            return null;

        const string rolePrefix = "/user/vive_tracker_htcx/role/";
        return userPath.StartsWith(rolePrefix, StringComparison.Ordinal)
            ? userPath[rolePrefix.Length..]
            : null;
    }

    private void AddTrackerSubactionPath(string? userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath) || _trackerSubactionPaths.ContainsKey(userPath))
            return;

        try
        {
            _trackerSubactionPaths[userPath] = StringToPathOrThrow(userPath);
        }
        catch (Exception ex)
        {
            Debug.Out($"OpenXR: tracker path '{userPath}' is not usable as an action subpath: {ex.Message}");
        }
    }

    private string? PathToString(XrPath path)
    {
        if (path == 0 || _instance.Handle == 0)
            return null;

        byte* buffer = stackalloc byte[512];
        uint count = 0;
        Result result = Api.PathToString(_instance, path, 512, ref count, buffer);
        if (result != Result.Success)
            return null;

        return Marshal.PtrToStringAnsi((nint)buffer);
    }

    private void LogHandTrackingUnavailable(string reason)
    {
        if (Interlocked.Exchange(ref _handTrackingUnavailableLogged, 1) != 0)
            return;

        Debug.LogWarning($"OpenXR hand tracking unavailable; hand skeleton queries will use controller-derived summaries when possible. Reason={reason}");
    }

    private void LogViveTrackerExtensionUnavailable(string reason)
    {
        if (Interlocked.Exchange(ref _viveTrackerExtensionUnavailableLogged, 1) != 0)
            return;

        Debug.LogWarning($"OpenXR Vive tracker extension unavailable; SteamVR tracker role poses require {HtcxViveTrackerInteraction.ExtensionName}. Reason={reason}");
    }

    private static float EstimateFingerCurl(RuntimeVrHandJointState[] joints, RuntimeVrHandJoint rootJoint, RuntimeVrHandJoint tipJoint)
    {
        RuntimeVrHandJointState root = joints[(int)rootJoint];
        RuntimeVrHandJointState tip = joints[(int)tipJoint];
        if (!root.PositionValid || !tip.PositionValid)
            return 0.0f;

        float distance = Vector3.Distance(root.Position, tip.Position);
        return Math.Clamp(1.0f - distance / 0.12f, 0.0f, 1.0f);
    }

    private XrAction GetAction(string category, string name)
        => _runtimeInputActions.TryGetValue(MakeRuntimeInputKey(category, name), out OpenXrRuntimeInputAction? action)
            ? action.Action
            : default;

    private bool TryGetRuntimeInputAction(
        string category,
        string name,
        RuntimeVrActionValueType valueType,
        [NotNullWhen(true)] out OpenXrRuntimeInputAction? action)
    {
        if (_runtimeInputActions.TryGetValue(MakeRuntimeInputKey(category, name), out action) &&
            action.ValueType == valueType)
        {
            return true;
        }

        action = null;
        return false;
    }

    private XrPath ResolveHapticSubactionPath(string name)
    {
        if (name.Contains("Left", StringComparison.OrdinalIgnoreCase))
            return _leftHandPath;
        if (name.Contains("Right", StringComparison.OrdinalIgnoreCase))
            return _rightHandPath;
        return default;
    }

    private static string MakeRuntimeInputKey(string category, string name)
        => string.Concat(category, "/", name);

    private static string MakeOpenXrActionName(string category, string name)
    {
        Span<char> buffer = stackalloc char[63];
        int length = 0;
        AppendActionNamePart(category, buffer, ref length);
        if (length < buffer.Length)
            buffer[length++] = '_';
        AppendActionNamePart(name, buffer, ref length);
        return new string(buffer[..length]);
    }

    private static void AppendActionNamePart(string value, Span<char> buffer, ref int length)
    {
        for (int i = 0; i < value.Length && length < buffer.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c) && length > 0 && buffer[length - 1] != '_')
                buffer[length++] = '_';
            if (length >= buffer.Length)
                break;

            buffer[length++] = char.IsLetterOrDigit(c)
                ? char.ToLowerInvariant(c)
                : '_';
        }
    }

    private sealed class OpenXrRuntimeInputAction(
        string category,
        string name,
        RuntimeVrActionValueType valueType,
        XrAction action,
        XrPath subactionPath)
    {
        public string Category { get; } = category;
        public string Name { get; } = name;
        public RuntimeVrActionValueType ValueType { get; } = valueType;
        public XrAction Action { get; } = action;
        public XrPath SubactionPath { get; } = subactionPath;
        public bool Active;
        public bool BoolValue;
        public float FloatValue;
        public Vector2 Vector2Value;
        public bool InactiveLogged;
        public bool ErrorLogged;
    }
}
