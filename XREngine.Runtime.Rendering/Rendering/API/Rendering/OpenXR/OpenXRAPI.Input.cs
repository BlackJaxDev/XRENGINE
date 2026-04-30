using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.HTCX;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Debug = XREngine.Debug;

using XrAction = Silk.NET.OpenXR.Action;
using XrPath = System.UInt64;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private ActionSet _inputActionSet;
    private XrAction _handGripPoseAction;
    private Space _leftHandGripSpace;
    private Space _rightHandGripSpace;

    private XrAction _trackerPoseAction;
    private readonly Dictionary<string, XrPath> _trackerSubactionPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Space> _trackerSpaces = new(StringComparer.Ordinal);

    private XrPath _leftHandPath;
    private XrPath _rightHandPath;

    private bool _inputAttached;
    private bool _inputCreated;

    private static readonly string[] DefaultViveTrackerRoleUserPaths =
    [
        "/user/vive_tracker_htcx/role/waist",
        "/user/vive_tracker_htcx/role/chest",
        "/user/vive_tracker_htcx/role/left_foot",
        "/user/vive_tracker_htcx/role/right_foot",
        "/user/vive_tracker_htcx/role/left_shoulder",
        "/user/vive_tracker_htcx/role/right_shoulder",
        "/user/vive_tracker_htcx/role/left_elbow",
        "/user/vive_tracker_htcx/role/right_elbow",
        "/user/vive_tracker_htcx/role/left_knee",
        "/user/vive_tracker_htcx/role/right_knee",
        "/user/vive_tracker_htcx/role/camera",
        "/user/vive_tracker_htcx/role/keyboard",
    ];

    private void EnsureInputCreated()
    {
        if (_inputCreated)
            return;

        if (_instance.Handle == 0 || _session.Handle == 0)
            return;

        try
        {
            CreateCorePaths();
            CreateActionSetAndActions();
            CreateActionSpaces();
            SuggestDefaultBindings();
            AttachActionSets();
            _inputCreated = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR input init failed: {ex.Message}");
        }
    }

    private void CreateCorePaths()
    {
        _leftHandPath = StringToPathOrThrow("/user/hand/left");
        _rightHandPath = StringToPathOrThrow("/user/hand/right");

        _trackerSubactionPaths.Clear();
        foreach (var rolePath in DefaultViveTrackerRoleUserPaths)
        {
            try
            {
                _trackerSubactionPaths[rolePath] = StringToPathOrThrow(rolePath);
            }
            catch
            {
                // Ignore unsupported role paths.
            }
        }

        lock (_openXrPoseLock)
        {
            foreach (var p in _trackerSubactionPaths.Keys)
                _openXrKnownTrackerPaths.Add(p);
        }

        // Best-effort: runtime may not expose the extension; we still keep default role paths.
        _ = Api.TryGetInstanceExtension<HtcxViveTrackerInteraction>(string.Empty, _instance, out _);
    }

    private static void WriteUtf8Z(byte* dest, int maxBytes, string text)
    {
        if (dest is null || maxBytes <= 0)
            return;

        var bytes = Encoding.UTF8.GetBytes(text);
        int len = Math.Min(bytes.Length, maxBytes - 1);
        for (int i = 0; i < len; i++)
            dest[i] = bytes[i];
        dest[len] = 0;

        for (int i = len + 1; i < maxBytes; i++)
            dest[i] = 0;
    }

    private XrPath StringToPathOrThrow(string s)
    {
        XrPath p = default;
        var r = Api.StringToPath(_instance, s, ref p);
        if (r != Result.Success)
            throw new Exception($"xrStringToPath('{s}') failed: {r}");
        return p;
    }

    private void CreateActionSetAndActions()
    {
        var actionSetInfo = new ActionSetCreateInfo
        {
            Type = StructureType.ActionSetCreateInfo,
            Priority = 0,
        };

        WriteUtf8Z(actionSetInfo.ActionSetName, 64, "xre_input");
        WriteUtf8Z(actionSetInfo.LocalizedActionSetName, 128, "XRE Input");

        var setResult = Api.CreateActionSet(_instance, in actionSetInfo, ref _inputActionSet);
        if (setResult != Result.Success)
            throw new Exception($"xrCreateActionSet failed: {setResult}");

        XrPath* handSubactionPaths = stackalloc XrPath[2] { _leftHandPath, _rightHandPath };
        var handPoseInfo = new ActionCreateInfo
        {
            Type = StructureType.ActionCreateInfo,
            ActionType = ActionType.PoseInput,
            CountSubactionPaths = 2,
            SubactionPaths = handSubactionPaths,
        };

        WriteUtf8Z(handPoseInfo.ActionName, 64, "hand_grip_pose");
        WriteUtf8Z(handPoseInfo.LocalizedActionName, 128, "Hand Grip Pose");

        var actionResult = Api.CreateAction(_inputActionSet, in handPoseInfo, ref _handGripPoseAction);
        if (actionResult != Result.Success)
            throw new Exception($"xrCreateAction(hand_grip_pose) failed: {actionResult}");

        if (_trackerSubactionPaths.Count > 0)
        {
            var trackerPaths = new XrPath[_trackerSubactionPaths.Count];
            int idx = 0;
            foreach (var p in _trackerSubactionPaths.Values)
                trackerPaths[idx++] = p;

            fixed (XrPath* trackerSubactions = trackerPaths)
            {
                var trackerPoseInfo = new ActionCreateInfo
                {
                    Type = StructureType.ActionCreateInfo,
                    ActionType = ActionType.PoseInput,
                    CountSubactionPaths = (uint)trackerPaths.Length,
                    SubactionPaths = trackerSubactions,
                };

                WriteUtf8Z(trackerPoseInfo.ActionName, 64, "tracker_pose");
                WriteUtf8Z(trackerPoseInfo.LocalizedActionName, 128, "Vive Tracker Pose");

                var trackerResult = Api.CreateAction(_inputActionSet, in trackerPoseInfo, ref _trackerPoseAction);
                if (trackerResult != Result.Success)
                    Debug.LogWarning($"xrCreateAction(tracker_pose) failed: {trackerResult}");
            }
        }
    }

    private void CreateActionSpaces()
    {
        var identity = new Posef
        {
            Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
            Position = new Vector3f { X = 0, Y = 0, Z = 0 }
        };

        var leftInfo = new ActionSpaceCreateInfo
        {
            Type = StructureType.ActionSpaceCreateInfo,
            Action = _handGripPoseAction,
            SubactionPath = _leftHandPath,
            PoseInActionSpace = identity,
        };

        var rightInfo = new ActionSpaceCreateInfo
        {
            Type = StructureType.ActionSpaceCreateInfo,
            Action = _handGripPoseAction,
            SubactionPath = _rightHandPath,
            PoseInActionSpace = identity,
        };

        var leftRes = Api.CreateActionSpace(_session, in leftInfo, ref _leftHandGripSpace);
        if (leftRes != Result.Success)
            Debug.LogWarning($"xrCreateActionSpace(left hand) failed: {leftRes}");

        var rightRes = Api.CreateActionSpace(_session, in rightInfo, ref _rightHandGripSpace);
        if (rightRes != Result.Success)
            Debug.LogWarning($"xrCreateActionSpace(right hand) failed: {rightRes}");

        _trackerSpaces.Clear();
        if (_trackerPoseAction.Handle != 0)
        {
            foreach (var (userPath, subactionPath) in _trackerSubactionPaths)
            {
                var trackerInfo = new ActionSpaceCreateInfo
                {
                    Type = StructureType.ActionSpaceCreateInfo,
                    Action = _trackerPoseAction,
                    SubactionPath = subactionPath,
                    PoseInActionSpace = identity,
                };

                Space trackerSpace = default;
                var trackerRes = Api.CreateActionSpace(_session, in trackerInfo, ref trackerSpace);
                if (trackerRes == Result.Success)
                    _trackerSpaces[userPath] = trackerSpace;
            }
        }
    }

    private void AttachActionSets()
    {
        if (_inputAttached)
            return;

        ActionSet* sets = stackalloc ActionSet[1] { _inputActionSet };
        var attachInfo = new SessionActionSetsAttachInfo
        {
            Type = StructureType.SessionActionSetsAttachInfo,
            CountActionSets = 1,
            ActionSets = sets,
        };

        var attachRes = Api.AttachSessionActionSets(_session, in attachInfo);
        if (attachRes != Result.Success)
        {
            Debug.LogWarning($"xrAttachSessionActionSets failed: {attachRes}");
            return;
        }

        _inputAttached = true;
    }

    private void SuggestDefaultBindings()
    {
        // Best-effort: missing suggested bindings may still work on some runtimes, but is not guaranteed.
        // We provide common bindings for grip pose across widely-used interaction profiles.
        try
        {
            XrPath leftGrip = StringToPathOrThrow("/user/hand/left/input/grip/pose");
            XrPath rightGrip = StringToPathOrThrow("/user/hand/right/input/grip/pose");

            var bindings = new ActionSuggestedBinding[2]
            {
                new ActionSuggestedBinding { Action = _handGripPoseAction, Binding = leftGrip },
                new ActionSuggestedBinding { Action = _handGripPoseAction, Binding = rightGrip },
            };

            SuggestForProfile("/interaction_profiles/khr/simple_controller", bindings);
            SuggestForProfile("/interaction_profiles/oculus/touch_controller", bindings);
            SuggestForProfile("/interaction_profiles/valve/index_controller", bindings);
            SuggestForProfile("/interaction_profiles/htc/vive_controller", bindings);
            SuggestForProfile("/interaction_profiles/microsoft/motion_controller", bindings);

            if (_trackerPoseAction.Handle != 0 && _trackerSubactionPaths.Count > 0)
            {
                // Bind each tracker role to its grip pose.
                var trackerBindings = new List<ActionSuggestedBinding>(_trackerSubactionPaths.Count);
                foreach (var role in _trackerSubactionPaths.Keys)
                {
                    try
                    {
                        XrPath binding = StringToPathOrThrow(role + "/input/grip/pose");
                        trackerBindings.Add(new ActionSuggestedBinding { Action = _trackerPoseAction, Binding = binding });
                    }
                    catch
                    {
                        // Ignore unsupported.
                    }
                }

                if (trackerBindings.Count > 0)
                {
                    SuggestForProfile("/interaction_profiles/htc/vive_tracker_htcx", trackerBindings.ToArray());
                }
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    private void SuggestForProfile(string profilePath, ActionSuggestedBinding[] bindings)
    {
        if (bindings.Length == 0)
            return;

        XrPath profile = StringToPathOrThrow(profilePath);
        fixed (ActionSuggestedBinding* bindingsPtr = bindings)
        {
            var suggested = new InteractionProfileSuggestedBinding
            {
                Type = StructureType.InteractionProfileSuggestedBinding,
                InteractionProfile = profile,
                CountSuggestedBindings = (uint)bindings.Length,
                SuggestedBindings = bindingsPtr,
            };

            var res = Api.SuggestInteractionProfileBinding(_instance, in suggested);
            if (res != Result.Success)
                Debug.Out($"OpenXR: SuggestBindings for '{profilePath}' => {res}");
        }
    }

    private void SyncActionsForFrame()
    {
        if (!_inputCreated || !_inputAttached)
            return;

        var active = new ActiveActionSet
        {
            ActionSet = _inputActionSet,
            SubactionPath = default,
        };

        var syncInfo = new ActionsSyncInfo
        {
            Type = StructureType.ActionsSyncInfo,
            CountActiveActionSets = 1,
            ActiveActionSets = &active,
        };

        var res = Api.SyncAction(_session, in syncInfo);
        if (res != Result.Success)
        {
            // Not fatal; poses will just be invalid this frame.
            Debug.Out($"OpenXR: SyncActions => {res}");
        }
    }

    private bool TryGetActivePoseState(XrAction poseAction, XrPath subactionPath, out bool isActive)
    {
        isActive = false;
        if (poseAction.Handle == 0)
            return false;

        var getInfo = new ActionStateGetInfo
        {
            Type = StructureType.ActionStateGetInfo,
            Action = poseAction,
            SubactionPath = subactionPath,
        };

        var state = new ActionStatePose
        {
            Type = StructureType.ActionStatePose,
        };

        var res = Api.GetActionStatePose(_session, in getInfo, ref state);
        if (res != Result.Success)
            return false;

        isActive = state.IsActive != 0;
        return true;
    }

    private bool TryLocateSpace(Space space, long displayTime, out Matrix4x4 localMatrix)
    {
        localMatrix = Matrix4x4.Identity;
        if (space.Handle == 0)
            return false;

        var location = new SpaceLocation { Type = StructureType.SpaceLocation };
        var res = Api.LocateSpace(space, _appSpace, displayTime, ref location);
        if (res != Result.Success)
            return false;

        const SpaceLocationFlags need = SpaceLocationFlags.PositionValidBit | SpaceLocationFlags.OrientationValidBit;
        if ((location.LocationFlags & need) != need)
            return false;

        var p = location.Pose;
        var pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
        var rot = Quaternion.Normalize(new Quaternion(p.Orientation.X, p.Orientation.Y, p.Orientation.Z, p.Orientation.W));
        localMatrix = Matrix4x4.CreateFromQuaternion(rot);
        localMatrix.Translation = pos;
        return true;
    }

    private void UpdateActionPoseCaches(OpenXrPoseTiming timing)
    {
        if (!_sessionBegun)
            return;

        EnsureInputCreated();
        if (!_inputCreated)
            return;

        // Poses are always located at the runtime's predicted display time for the current frame.
        long displayTime = _frameState.PredictedDisplayTime;

        SyncActionsForFrame();

        bool leftActive = false;
        bool rightActive = false;
        _ = TryGetActivePoseState(_handGripPoseAction, _leftHandPath, out leftActive);
        _ = TryGetActivePoseState(_handGripPoseAction, _rightHandPath, out rightActive);

        Matrix4x4 leftLocal = Matrix4x4.Identity;
        Matrix4x4 rightLocal = Matrix4x4.Identity;
        bool leftValid = leftActive && TryLocateSpace(_leftHandGripSpace, displayTime, out leftLocal);
        bool rightValid = rightActive && TryLocateSpace(_rightHandGripSpace, displayTime, out rightLocal);

        lock (_openXrPoseLock)
        {
            if (timing == OpenXrPoseTiming.Late)
            {
                _openXrLateLeftControllerValid = leftValid ? 1 : 0;
                _openXrLateRightControllerValid = rightValid ? 1 : 0;
                if (leftValid)
                    _openXrLateLeftControllerLocalPose = leftLocal;
                if (rightValid)
                    _openXrLateRightControllerLocalPose = rightLocal;
            }
            else
            {
                _openXrPredLeftControllerValid = leftValid ? 1 : 0;
                _openXrPredRightControllerValid = rightValid ? 1 : 0;
                if (leftValid)
                    _openXrPredLeftControllerLocalPose = leftLocal;
                if (rightValid)
                    _openXrPredRightControllerLocalPose = rightLocal;
            }
        }

        if (_trackerPoseAction.Handle != 0 && _trackerSpaces.Count > 0)
        {
            lock (_openXrPoseLock)
            {
                var dict = timing == OpenXrPoseTiming.Late ? _openXrLateTrackerLocalPose : _openXrPredTrackerLocalPose;
                dict.Clear();

                foreach (var (userPath, space) in _trackerSpaces)
                {
                    if (TryLocateSpace(space, displayTime, out var mtx))
                        dict[userPath] = mtx;
                }
            }
        }
    }

    private void DestroyInput()
    {
        try
        {
            foreach (var s in _trackerSpaces.Values)
                if (s.Handle != 0)
                    Api.DestroySpace(s);
            _trackerSpaces.Clear();

            if (_leftHandGripSpace.Handle != 0)
                Api.DestroySpace(_leftHandGripSpace);
            if (_rightHandGripSpace.Handle != 0)
                Api.DestroySpace(_rightHandGripSpace);

            if (_handGripPoseAction.Handle != 0)
                Api.DestroyAction(_handGripPoseAction);
            if (_trackerPoseAction.Handle != 0)
                Api.DestroyAction(_trackerPoseAction);

            if (_inputActionSet.Handle != 0)
                Api.DestroyActionSet(_inputActionSet);
        }
        catch
        {
            // Best-effort only.
        }
        finally
        {
            _leftHandGripSpace = default;
            _rightHandGripSpace = default;
            _handGripPoseAction = default;
            _trackerPoseAction = default;
            _inputActionSet = default;
            _inputAttached = false;
            _inputCreated = false;
        }
    }
}
