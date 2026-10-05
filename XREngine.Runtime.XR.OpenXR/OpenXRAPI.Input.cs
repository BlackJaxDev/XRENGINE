using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.HTCX;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using XREngine.Input;
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

    private bool _controllerProfilesDirty = true;
    private string? _leftControllerInteractionProfile, _rightControllerInteractionProfile;
    public string? GetControllerInteractionProfile(bool leftHand)
        => leftHand ? Volatile.Read(ref _leftControllerInteractionProfile) : Volatile.Read(ref _rightControllerInteractionProfile);

    private void RefreshControllerInteractionProfiles()
    {
        var profile = new InteractionProfileState { Type = StructureType.InteractionProfileState };
        if (Api.GetCurrentInteractionProfile(_session, _leftHandPath, ref profile) == Result.Success)
            Volatile.Write(ref _leftControllerInteractionProfile, PathToString(profile.InteractionProfile));
        profile = new InteractionProfileState { Type = StructureType.InteractionProfileState };
        if (Api.GetCurrentInteractionProfile(_session, _rightHandPath, ref profile) == Result.Success)
            Volatile.Write(ref _rightControllerInteractionProfile, PathToString(profile.InteractionProfile));
        _controllerProfilesDirty = false;
    }

    private XrPath _leftHandPath;
    private XrPath _rightHandPath;
    private XrPath _leftInteractionProfilePath;
    private XrPath _rightInteractionProfilePath;
    private string? _leftInteractionProfile;
    private string? _rightInteractionProfile;

    /// <summary>Currently active interaction profile for the given hand, if the runtime reports one.</summary>
    public string? GetCurrentInteractionProfile(bool leftHand)
    {
        lock (_openXrPoseLock)
            return leftHand ? _leftInteractionProfile : _rightInteractionProfile;
    }

    private void UpdateCurrentInteractionProfiles()
    {
        UpdateCurrentInteractionProfile(_leftHandPath, leftHand: true);
        UpdateCurrentInteractionProfile(_rightHandPath, leftHand: false);
    }

    private void UpdateCurrentInteractionProfile(XrPath handPath, bool leftHand)
    {
        var state = new InteractionProfileState { Type = StructureType.InteractionProfileState };
        if (Api.GetCurrentInteractionProfile(_session, handPath, ref state) != Result.Success)
            return;

        lock (_openXrPoseLock)
        {
            XrPath previous = leftHand ? _leftInteractionProfilePath : _rightInteractionProfilePath;
            if (previous == state.InteractionProfile)
                return;

            string? profile = PathToString(state.InteractionProfile);
            if (leftHand)
            {
                _leftInteractionProfilePath = state.InteractionProfile;
                _leftInteractionProfile = profile;
            }
            else
            {
                _rightInteractionProfilePath = state.InteractionProfile;
                _rightInteractionProfile = profile;
            }
        }
    }

    private bool _inputAttached;
    private bool _inputCreated;
    private int _trackerRefreshRequested;
    private int _trackerRefreshEnumerated;
    private int _lateTrackerRefreshRequired;

    /// <summary>Whether a discovered tracker needs action-set recreation before it can stream poses.</summary>
    public bool IsTrackerRefreshPending =>
        Volatile.Read(ref _trackerRefreshRequested) != 0 || Volatile.Read(ref _lateTrackerRefreshRequired) != 0;

    /// <summary>Requests a tracker check at the next safe frame boundary after the player opens calibration.</summary>
    public void RequestTrackerRefreshForCalibration()
    {
        Volatile.Write(ref _trackerRefreshEnumerated, 0);
        Volatile.Write(ref _nextViveTrackerEnumerationTick, 0);
        Volatile.Write(ref _trackerRefreshRequested, 1);
    }

    private void ServiceTrackerRefreshForCalibration()
    {
        AssertOpenXrRenderThread(nameof(ServiceTrackerRefreshForCalibration));
        if (Volatile.Read(ref _trackerRefreshRequested) == 0 || !_sessionBegun)
            return;

        if (Volatile.Read(ref _trackerRefreshEnumerated) == 0)
        {
            if (Environment.TickCount64 < Volatile.Read(ref _nextViveTrackerEnumerationTick))
                return;
            if (!EnumerateViveTrackerPaths())
                return;
            Volatile.Write(ref _trackerRefreshEnumerated, 1);
        }
        if (Volatile.Read(ref _lateTrackerRefreshRequired) == 0 || !HasConnectedTrackerWithoutActionSpace())
        {
            Volatile.Write(ref _trackerRefreshRequested, 0);
            Volatile.Write(ref _trackerRefreshEnumerated, 0);
            Volatile.Write(ref _lateTrackerRefreshRequired, 0);
            return;
        }

        if (!CanReplaceOpenXrSwapchainsInSession())
            return;

        Debug.Out("OpenXR: refreshing tracker action subpaths after calibration opened.");
        if (!TearDownSessionResourcesOnOwningThread(destroyInstance: false))
        {
            SetRuntimeState(OpenXrRuntimeState.SessionStopping);
            return;
        }

        Volatile.Write(ref _trackerRefreshRequested, 0);
        Volatile.Write(ref _trackerRefreshEnumerated, 0);
        Volatile.Write(ref _lateTrackerRefreshRequired, 0);
        _nextProbeUtc = DateTime.UtcNow;
        SetRuntimeState(OpenXrRuntimeState.DesktopOnly);
    }

    private bool HasConnectedTrackerWithoutActionSpace()
    {
        lock (_openXrPoseLock)
        {
            foreach (RuntimeVrTrackerInfo info in _openXrKnownTrackers.Values)
            {
                if (info.Connected && (info.RequiresInputRebuild ||
                    (_trackerSubactionPaths.ContainsKey(info.UserPath) && !_trackerSpaces.ContainsKey(info.UserPath))))
                    return true;
            }
            return false;
        }
    }

    private void ServiceTrackerDiscovery()
    {
        AssertOpenXrRenderThread(nameof(ServiceTrackerDiscovery));
        if (_sessionBegun && _viveTrackerInteraction is not null &&
            Volatile.Read(ref _trackerRefreshRequested) == 0 &&
            Environment.TickCount64 >= Volatile.Read(ref _nextViveTrackerEnumerationTick))
            EnumerateViveTrackerPaths();
    }
    private uint _viveTrackerExtensionRevision;

    private static readonly string[] DefaultViveTrackerRoleUserPaths =
    [
        "/user/vive_tracker_htcx/role/handheld_object",
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
        "/user/vive_tracker_htcx/role/handheld_object",
        "/user/vive_tracker_htcx/role/left_wrist",
        "/user/vive_tracker_htcx/role/right_wrist",
        "/user/vive_tracker_htcx/role/left_ankle",
        "/user/vive_tracker_htcx/role/right_ankle",
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
            _inputCreated = _inputAttached;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR input init failed: {ex.Message}");
            DestroyInput();
        }
    }

    private void CreateCorePaths()
    {
        _controllerProfilesDirty = true;
        _leftHandPath = StringToPathOrThrow("/user/hand/left");
        _rightHandPath = StringToPathOrThrow("/user/hand/right");

        _trackerSubactionPaths.Clear();
        _openXrTrackingSessionGeneration = Interlocked.Increment(ref _nextTrackingSessionGeneration);
        _openXrLastTrackerDiagnostics = [];
        InitializeViveTrackerExtension();
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
                if (trackerResult == Result.ErrorPathUnsupported)
                {
                    Debug.Out("OpenXR: persistent tracker subaction paths are unsupported; tracker pose input is unavailable for this session. No alternate provider has been enabled.");
                    _trackerPoseAction = default;
                }
                else if (trackerResult != Result.Success)
                {
                    Debug.LogWarning($"xrCreateAction(tracker_pose) failed: {trackerResult}");
                    _trackerPoseAction = default;
                }
            }
        }

        CreateRuntimeNeutralInputActions();
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

        CreateRuntimeNeutralActionSpaces();
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
            throw new InvalidOperationException($"xrAttachSessionActionSets failed: {attachRes}");
        }

        _inputAttached = true;
    }

    private void SuggestDefaultBindings()
    {
        // Best-effort: missing suggested bindings may still work on some runtimes, but is not guaranteed.
        // Submit one complete binding table per controller profile. Some runtimes treat a later
        // suggestion for the same profile as replacing the previous table, so grip/aim/buttons
        // must be suggested together.
        try
        {
            SuggestRuntimeNeutralBindings();

            if (_trackerPoseAction.Handle != 0 && _trackerSubactionPaths.Count > 0)
            {
                // The interaction profile only accepts role component paths. Persistent
                // physical paths are subaction selectors, never suggested component bindings.
                var trackerBindings = new List<ActionSuggestedBinding>(DefaultViveTrackerRoleUserPaths.Length);
                foreach (var role in DefaultViveTrackerRoleUserPaths)
                {
                    if (_viveTrackerExtensionRevision < 3 && (role.EndsWith("_wrist", StringComparison.Ordinal) || role.EndsWith("_ankle", StringComparison.Ordinal)))
                        continue;
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
            if (profilePath == "/interaction_profiles/htc/vive_tracker_htcx")
                Debug.Out($"OpenXR tracker bindings: revision={_viveTrackerExtensionRevision}, roleBindingCount={bindings.Length}, persistentSubactionCount={_trackerSubactionPaths.Count}, result={res}");
            if (res != Result.Success)
                Debug.Out($"OpenXR: SuggestBindings for '{profilePath}' => {res}");
        }
    }

    private bool SyncActionsForFrame()
    {
        AssertOpenXrRenderThread(nameof(SyncActionsForFrame));
        if (!_inputCreated || !_inputAttached)
            return false;

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
            return false;
        }

        if (_controllerProfilesDirty)
            RefreshControllerInteractionProfiles();
        return true;
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
        => TryLocateSpace(space, displayTime, out localMatrix, out _, out _);

    private bool TryLocateSpace(Space space, long displayTime, out Matrix4x4 localMatrix,
        out bool positionValid, out bool orientationValid)
    {
        positionValid = false;
        orientationValid = false;
        AssertOpenXrRenderThread(nameof(TryLocateSpace));
        localMatrix = Matrix4x4.Identity;
        positionValid = false;
        orientationValid = false;
        if (space.Handle == 0)
            return false;

        var location = new SpaceLocation { Type = StructureType.SpaceLocation };
        var res = Api.LocateSpace(space, _appSpace, displayTime, ref location);
        if (res != Result.Success)
            return false;

        positionValid = (location.LocationFlags & SpaceLocationFlags.PositionValidBit) != 0;
        orientationValid = (location.LocationFlags & SpaceLocationFlags.OrientationValidBit) != 0;
        const SpaceLocationFlags need = SpaceLocationFlags.PositionValidBit | SpaceLocationFlags.OrientationValidBit;
        if ((location.LocationFlags & need) != need)
            return false;

        var p = location.Pose;
        var pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
        var rawRotation = new Quaternion(p.Orientation.X, p.Orientation.Y, p.Orientation.Z, p.Orientation.W);
        float rotationLengthSquared = rawRotation.LengthSquared();
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z) ||
            !float.IsFinite(rotationLengthSquared) || rotationLengthSquared < 0.000001f)
        {
            positionValid = false;
            orientationValid = false;
            return false;
        }
        var rot = Quaternion.Normalize(rawRotation);
        localMatrix = Matrix4x4.CreateFromQuaternion(rot);
        localMatrix.Translation = pos;
        return true;
    }

    private bool IsTrackerInteractionProfileBound(XrPath path)
    {
        var profile = new InteractionProfileState { Type = StructureType.InteractionProfileState };
        return Api.GetCurrentInteractionProfile(_session, path, ref profile) == Result.Success && profile.InteractionProfile != 0;
    }

    private void UpdateActionPoseCaches(OpenXrPoseTiming timing)
    {
        AssertOpenXrRenderThread(nameof(UpdateActionPoseCaches));
        if (!_sessionBegun)
            return;

        EnsureInputCreated();
        if (!_inputCreated)
            return;

        // Poses are located at the frame display time plus the optional app-level prediction bias.
        long displayTime = ResolveOpenXrPoseDisplayTime(timing);
        if (timing == OpenXrPoseTiming.Predicted && _openXrPendingReferenceSpaceChangeTime != 0 && displayTime >= _openXrPendingReferenceSpaceChangeTime)
        {
            _openXrPendingReferenceSpaceChangeTime = 0;
            _openXrTrackingReferenceSpaceVersion++;
        }

        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        bool shouldSyncActions = timing == OpenXrPoseTiming.Predicted
            || OpenXrActionSyncHandling == global::XREngine.Rendering.API.Rendering.OpenXR.OpenXrActionSyncPolicy.PredictedAndLate
            || Volatile.Read(ref _openXrActionsSyncedFrameNumber) != frameNo;
        if (shouldSyncActions)
        {
            Volatile.Write(ref _openXrActionsSyncedFrameNumber, SyncActionsForFrame() ? frameNo : -1);
        }

        bool synchronized = Volatile.Read(ref _openXrActionsSyncedFrameNumber) == frameNo;
        if (!synchronized)
        {
            lock (_openXrPoseLock)
            {
                _openXrPredLeftControllerValid = _openXrPredRightControllerValid = 0;
                _openXrLateLeftControllerValid = _openXrLateRightControllerValid = 0;
                _openXrPredInputFrameNumber = _openXrLateInputFrameNumber = 0;
                _openXrPredInputPublicationTimestamp = 0;
                _openXrPredTrackerLocalPose.Clear();
                _openXrLateTrackerLocalPose.Clear();
                _openXrTrackingPublicationTimestamp = 0;
            }
            for (int i = 0; i < _runtimeInputActionList.Count; i++)
                _runtimeInputActionList[i].Active = false;
            return;
        }

        if (timing == OpenXrPoseTiming.Predicted)
            UpdateCurrentInteractionProfiles();

        bool leftActive = synchronized && TryGetActivePoseState(_handGripPoseAction, _leftHandPath, out bool leftActionActive) && leftActionActive;
        bool rightActive = synchronized && TryGetActivePoseState(_handGripPoseAction, _rightHandPath, out bool rightActionActive) && rightActionActive;
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
                if (leftValid) _openXrLateLeftControllerLocalPose = leftLocal;
                if (rightValid) _openXrLateRightControllerLocalPose = rightLocal;
            }
            else
            {
                _openXrPredLeftControllerValid = leftValid ? 1 : 0;
                _openXrPredRightControllerValid = rightValid ? 1 : 0;
                if (leftValid) _openXrPredLeftControllerLocalPose = leftLocal;
                if (rightValid) _openXrPredRightControllerLocalPose = rightLocal;
            }

            var poses = timing == OpenXrPoseTiming.Late ? _openXrLateTrackerLocalPose : _openXrPredTrackerLocalPose;
            poses.Clear();
            long snapshotId = timing == OpenXrPoseTiming.Predicted ? ++_openXrTrackingSnapshotId : _openXrTrackingSnapshotId;
            foreach (var entry in _openXrKnownTrackers)
            {
                RuntimeVrTrackerInfo previous = entry.Value;
                bool hasSpace = _trackerSpaces.TryGetValue(entry.Key, out Space space) && _trackerSubactionPaths.TryGetValue(entry.Key, out _);
                bool active = synchronized && hasSpace && TryGetActivePoseState(_trackerPoseAction, _trackerSubactionPaths[entry.Key], out bool actionActive) && actionActive;
                bool bound = hasSpace && (active || IsTrackerInteractionProfileBound(_trackerSubactionPaths[entry.Key]));
                bool positionValid = false;
                bool orientationValid = false;
                Matrix4x4 pose = previous.LastValidPose;
                bool valid = previous.Connected && active && TryLocateSpace(space, displayTime, out pose, out positionValid, out orientationValid);
                if (valid)
                    poses[entry.Key] = pose;

                if (timing == OpenXrPoseTiming.Predicted)
                {
                    RuntimeVrTrackerInfo current = previous with
                    {
                        PoseAvailable = valid,
                        Bound = bound,
                        ActionActive = active,
                        PositionValid = positionValid,
                        OrientationValid = orientationValid,
                        SnapshotId = snapshotId,
                        SampleTime = displayTime,
                        EverTracked = previous.EverTracked || valid,
                        HasLastValidPose = previous.HasLastValidPose || valid,
                        LastValidSnapshotId = valid ? snapshotId : previous.LastValidSnapshotId,
                        LastValidSampleTime = valid ? displayTime : previous.LastValidSampleTime,
                        LastValidPose = valid ? pose : previous.LastValidPose,
                    };
                    _openXrKnownTrackers[entry.Key] = current;
                    if (current.PoseCurrentlyUsable != previous.PoseCurrentlyUsable || current.ActionActive != previous.ActionActive)
                        Debug.Out($"OpenXR tracker sample: identity={entry.Key}, snapshot={snapshotId}, time={displayTime}, state={current.DiagnosticState}, positionValid={positionValid}, orientationValid={orientationValid}");
                }
            }
            if (timing == OpenXrPoseTiming.Predicted)
                PublishTrackingSnapshotLocked(displayTime);
            else
                _openXrLateActionSampleTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        lock (_openXrPoseLock)
        {
            if (timing == OpenXrPoseTiming.Predicted)
            {
                _openXrPredInputFrameNumber = frameNo;
                _openXrPredInputSampleTime = displayTime;
                _openXrPredInputPublicationTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            else
                _openXrLateInputFrameNumber = frameNo;
        }
        UpdateRuntimeNeutralInputStateCaches(displayTime, timing);
        RecordSmokeActionPoseCache(timing);
    }

    private void DestroyInput()
    {
        try
        {
            DestroyRuntimeNeutralInput();

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
            _trackerSubactionPaths.Clear();
            lock (_openXrPoseLock)
            {
                if (_openXrKnownTrackers.Count > 0)
                    _openXrLastTrackerDiagnostics = [.. _openXrKnownTrackers.Values];
                _openXrKnownTrackerPaths.Clear();
                _openXrKnownTrackers.Clear();
                _openXrPredTrackerLocalPose.Clear();
                _openXrLateTrackerLocalPose.Clear();
                _openXrPredInputPublicationTimestamp = 0;
                _openXrPublishedTrackerPoses.Clear();
                _openXrPublishedTrackingSnapshot = default;
                _openXrPredHeadValid = _openXrLateHeadValid = false;
                _openXrPredLeftControllerValid = _openXrPredRightControllerValid = 0;
                _openXrLateLeftControllerValid = _openXrLateRightControllerValid = 0;
                _openXrTrackingPublicationTimestamp = _openXrLateActionSampleTimestamp = 0;
                _openXrPendingReferenceSpaceChangeTime = 0;
            }
            _inputAttached = false;
            _inputCreated = false;
            _openXrPredInputFrameNumber = 0;
            _openXrLateInputFrameNumber = 0;
            _openXrPredInputSampleTime = 0;
            _leftInteractionProfilePath = default;
            _rightInteractionProfilePath = default;
            _leftInteractionProfile = null;
            _rightInteractionProfile = null;
            Volatile.Write(ref _lateTrackerRefreshRequired, 0);
        }
    }
}
