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
                if (!info.Connected)
                    continue;

                bool hasSpace = TryResolveTrackerTransportSpaceLocked(info, out _, out _, out bool duplicateRoleConflict);
                bool roleSpaceExists = !string.IsNullOrWhiteSpace(info.RolePath) && _trackerSpaces.ContainsKey(info.RolePath);
                if (duplicateRoleConflict || roleSpaceExists)
                    continue;

                if (info.RequiresInputRebuild || !hasSpace)
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
    private bool _trackerPersistentSubactionsAccepted;
    private int _loggedEmptyTrackerEnumerationAfterAttach;
    private int _loggedActiveTransportRoleWithoutEnumeration;
    private int _loggedPoseActionStateError;
    private int _loggedTrackerRoleProbeSummary;
    private long _trackerRoleProbeStartTick;
    private readonly HashSet<string> _loggedDuplicateTrackerRoleConflicts = new(StringComparer.Ordinal);

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
        "/user/vive_tracker_htcx/role/left_wrist",
        "/user/vive_tracker_htcx/role/right_wrist",
        "/user/vive_tracker_htcx/role/left_ankle",
        "/user/vive_tracker_htcx/role/right_ankle",
    ];

    private bool IsViveTrackerRoleSupported(string rolePath)
        => _viveTrackerExtensionRevision >= 3
            || (!rolePath.EndsWith("_wrist", StringComparison.Ordinal)
                && !rolePath.EndsWith("_ankle", StringComparison.Ordinal));

    private static bool IsViveTrackerRolePath(string userPath)
        => userPath.StartsWith("/user/vive_tracker_htcx/role/", StringComparison.Ordinal);

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
            if (_inputAttached && _viveTrackerInteraction is not null)
                EnumerateViveTrackerPaths();
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
        _trackerPersistentSubactionsAccepted = false;
        _loggedEmptyTrackerEnumerationAfterAttach = 0;
        _loggedActiveTransportRoleWithoutEnumeration = 0;
        _loggedPoseActionStateError = 0;
        _loggedTrackerRoleProbeSummary = 0;
        _trackerRoleProbeStartTick = Environment.TickCount64;
        _loggedDuplicateTrackerRoleConflicts.Clear();
        _openXrTrackingSessionGeneration = Interlocked.Increment(ref _nextTrackingSessionGeneration);
        _openXrLastTrackerDiagnostics = [];
        InitializeViveTrackerExtension();
        AddSupportedViveTrackerTransportSubactionPaths();
    }

    private void AddSupportedViveTrackerTransportSubactionPaths()
    {
        if (_viveTrackerInteraction is null)
            return;

        // Always register HTCX transport role subaction paths before binding suggestion so
        // runtimes that expose trackers only after /interaction_profiles/htc/vive_tracker_htcx
        // is suggested can activate tracker poses on the first session.
        foreach (string rolePath in DefaultViveTrackerRoleUserPaths)
        {
            if (IsViveTrackerRoleSupported(rolePath))
                AddTrackerSubactionPath(rolePath);
        }
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
            CreateTrackerPoseAction();

        CreateRuntimeNeutralInputActions();
        CreateEyeGazeAction();
    }

    private void CreateTrackerPoseAction()
    {
        bool hadPersistentPaths = false;
        foreach (string userPath in _trackerSubactionPaths.Keys)
        {
            if (!IsViveTrackerRolePath(userPath))
            {
                hadPersistentPaths = true;
                break;
            }
        }

        Result trackerResult = TryCreateTrackerPoseAction(_trackerSubactionPaths);
        if (trackerResult == Result.Success)
        {
            _trackerPersistentSubactionsAccepted = hadPersistentPaths;
            return;
        }

        if (trackerResult != Result.Success && hadPersistentPaths)
        {
            Debug.Out($"OpenXR: runtime rejected persistent tracker subaction paths ({trackerResult}); retrying tracker_pose with HTCX transport paths while keeping persistent hardware identity.");
            var toRemove = new List<string>();
            foreach (string userPath in _trackerSubactionPaths.Keys)
            {
                if (!IsViveTrackerRolePath(userPath))
                    toRemove.Add(userPath);
            }

            foreach (string userPath in toRemove)
                _trackerSubactionPaths.Remove(userPath);

            if (_trackerSubactionPaths.Count > 0)
            {
                trackerResult = TryCreateTrackerPoseAction(_trackerSubactionPaths);
                if (trackerResult == Result.Success)
                {
                    _trackerPersistentSubactionsAccepted = false;
                    return;
                }
            }
        }

        Debug.LogWarning($"xrCreateAction(tracker_pose) failed: {trackerResult}");
        _trackerPoseAction = default;
        _trackerPersistentSubactionsAccepted = false;
    }

    private Result TryCreateTrackerPoseAction(Dictionary<string, XrPath> subactionMap)
    {
        var trackerPaths = new XrPath[subactionMap.Count];
        int idx = 0;
        foreach (XrPath p in subactionMap.Values)
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

            _trackerPoseAction = default;
            return Api.CreateAction(_inputActionSet, in trackerPoseInfo, ref _trackerPoseAction);
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

        CreateRuntimeNeutralActionSpaces();
        CreateEyeGazeActionSpace();
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
            SuggestEyeGazeBinding();

            if (_trackerPoseAction.Handle != 0 && _viveTrackerInteraction is not null)
            {
                // The interaction profile only accepts role component paths. Proximity calibration
                // assigns avatar body slots independently of the HTCX transport path.
                var trackerBindings = new List<ActionSuggestedBinding>(DefaultViveTrackerRoleUserPaths.Length);
                foreach (var role in DefaultViveTrackerRoleUserPaths)
                {
                    if (!IsViveTrackerRoleSupported(role))
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
                Debug.Out($"OpenXR tracker bindings: revision={_viveTrackerExtensionRevision}, roleBindingCount={bindings.Length}, subactionCount={_trackerSubactionPaths.Count}, result={res}");
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
        {
            if (Interlocked.Exchange(ref _loggedPoseActionStateError, 1) == 0)
                Debug.LogWarning($"OpenXR: xrGetActionStatePose failed: {res}");
            return false;
        }

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
            ClearEyeGazePoseCache();
            return;
        }

        if (timing == OpenXrPoseTiming.Predicted)
        {
            UpdateCurrentInteractionProfiles();
            UpdateEyeGazePoseCache(displayTime, frameNo);
        }

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
                bool hasSpace = TryResolveTrackerTransportSpaceLocked(
                    previous,
                    out XrPath subactionPath,
                    out Space space,
                    out bool duplicateRoleConflict);
                if (duplicateRoleConflict && !string.IsNullOrEmpty(previous.RolePath) && _loggedDuplicateTrackerRoleConflicts.Add(previous.RolePath))
                    Debug.LogWarning($"OpenXR tracker transport conflict: multiple enumerated trackers share HTCX transport role '{previous.RolePath}'. Persistent-path subactions are unavailable, so only the primary tracker on this role reads the role space; secondary paired trackers on the same role are withheld to avoid duplicating one pose onto two identities.");
                bool active = synchronized && hasSpace && TryGetActivePoseState(_trackerPoseAction, subactionPath, out bool actionActive) && actionActive;
                bool bound = hasSpace && (active || IsTrackerInteractionProfileBound(subactionPath));
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
                    {
                        Debug.Out($"OpenXR tracker sample: identity={entry.Key}, snapshot={snapshotId}, time={displayTime}, state={current.DiagnosticState}, positionValid={positionValid}, orientationValid={orientationValid}");
                        Vector3 trackerPos = pose.Translation;
                        Vector3 headPos = _openXrPredHeadLocalPose.Translation;
                        Vector3 leftPos = _openXrPredLeftControllerLocalPose.Translation;
                        Vector3 rightPos = _openXrPredRightControllerLocalPose.Translation;
                        Debug.WriteAuxiliaryLog(
                            "openxr-trackers.log",
                            $"TrackerSample id={entry.Key} role={current.RolePath ?? "<none>"} dupConflict={duplicateRoleConflict} active={active} valid={valid} state={current.DiagnosticState} " +
                            $"pos=({trackerPos.X:F3},{trackerPos.Y:F3},{trackerPos.Z:F3}) head=({headPos.X:F3},{headPos.Y:F3},{headPos.Z:F3}) " +
                            $"leftHand=({leftPos.X:F3},{leftPos.Y:F3},{leftPos.Z:F3}) rightHand=({rightPos.X:F3},{rightPos.Y:F3},{rightPos.Z:F3})");
                    }
                }
            }
            if (timing == OpenXrPoseTiming.Predicted && _trackerPoseAction.Handle != 0 && synchronized)
            {
                if (_openXrKnownTrackers.Count == 0)
                    ProbeUnenumeratedTrackerTransportRolesLocked(displayTime);
                PromoteActiveUnclaimedTrackerRolesLocked(displayTime, snapshotId, poses);
                LogTrackerRoleProbeOnceLocked(displayTime);
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

    private bool TryResolveTrackerTransportSpaceLocked(
        in RuntimeVrTrackerInfo tracker,
        out XrPath subactionPath,
        out Space space,
        out bool duplicateRoleConflict)
    {
        duplicateRoleConflict = false;
        string? persistentPath = tracker.PersistentPath;
        if (!string.IsNullOrWhiteSpace(persistentPath)
            && _trackerSpaces.TryGetValue(persistentPath, out space)
            && _trackerSubactionPaths.TryGetValue(persistentPath, out subactionPath))
        {
            return true;
        }

        string? rolePath = tracker.RolePath;
        if (!string.IsNullOrWhiteSpace(rolePath)
            && _trackerSpaces.TryGetValue(rolePath, out space)
            && _trackerSubactionPaths.TryGetValue(rolePath, out subactionPath))
        {
            // SteamVR enumerates all paired trackers (including powered-off spares) that share
            // an HTCX transport role while rejecting persistent-path subactions. Assign each
            // role space to exactly one deterministic primary tracker identity (preferring an
            // already-tracked identity, then the first enumerated identity) and withhold only
            // secondary duplicate identities so active trackers still stream without duplicating
            // one pose onto two identities.
            string? primaryPersistentPath = null;
            bool primaryEverTracked = false;
            int connectedSharingRole = 0;
            foreach (RuntimeVrTrackerInfo candidate in _openXrKnownTrackers.Values)
            {
                if (!candidate.Connected
                    || !string.Equals(candidate.RolePath, rolePath, StringComparison.Ordinal))
                    continue;

                connectedSharingRole++;
                if (primaryPersistentPath is null
                    || (candidate.EverTracked && !primaryEverTracked))
                {
                    primaryPersistentPath = candidate.PersistentPath;
                    primaryEverTracked = candidate.EverTracked;
                }
            }

            if (connectedSharingRole > 1)
            {
                duplicateRoleConflict = true;
                if (!string.Equals(primaryPersistentPath, persistentPath, StringComparison.Ordinal))
                {
                    subactionPath = default;
                    space = default;
                    return false;
                }
            }

            return true;
        }

        subactionPath = default;
        space = default;
        return false;
    }

    private void PromoteActiveUnclaimedTrackerRolesLocked(
        long displayTime,
        long snapshotId,
        Dictionary<string, Matrix4x4> poses)
    {
        // Some runtimes activate an HTCX role pose space for a connected tracker whose
        // persistent entry was omitted or reported without a role path. Register any live,
        // unclaimed HTCX role so its pose streams without duplicating claimed roles.
        foreach (KeyValuePair<string, Space> entry in _trackerSpaces)
        {
            string rolePath = entry.Key;
            if (!IsViveTrackerRolePath(rolePath) || _openXrKnownTrackers.ContainsKey(rolePath))
                continue;
            if (!_trackerSubactionPaths.TryGetValue(rolePath, out XrPath subactionPath))
                continue;

            bool claimed = false;
            foreach (RuntimeVrTrackerInfo known in _openXrKnownTrackers.Values)
            {
                if (known.Connected && string.Equals(known.RolePath, rolePath, StringComparison.Ordinal))
                {
                    claimed = true;
                    break;
                }
            }
            if (claimed)
                continue;

            if (!TryGetActivePoseState(_trackerPoseAction, subactionPath, out bool active) || !active)
                continue;
            if (!TryLocateSpace(entry.Value, displayTime, out Matrix4x4 pose, out bool posValid, out bool oriValid))
                continue;

            _openXrKnownTrackerPaths.Add(rolePath);
            _openXrKnownTrackers[rolePath] = new RuntimeVrTrackerInfo
            {
                UserPath = rolePath,
                PersistentPath = rolePath,
                RolePath = rolePath,
                RoleName = GetViveTrackerRoleName(rolePath),
                RuntimeReported = true,
                Connected = true,
                Bound = true,
                ActionActive = true,
                PoseAvailable = true,
                PositionValid = posValid,
                OrientationValid = oriValid,
                SessionGeneration = _openXrTrackingSessionGeneration,
                SnapshotId = snapshotId,
                SampleTime = displayTime,
                EverTracked = true,
                HasLastValidPose = true,
                LastValidSnapshotId = snapshotId,
                LastValidSampleTime = displayTime,
                LastValidPose = pose,
            };
            poses[rolePath] = pose;
            Vector3 p = pose.Translation;
            Debug.WriteAuxiliaryLog(
                "openxr-trackers.log",
                $"UnclaimedActiveRole role={rolePath} pos=({p.X:F3},{p.Y:F3},{p.Z:F3})");
        }
    }

    private void LogTrackerRoleProbeOnceLocked(long displayTime)
    {
        if (_loggedTrackerRoleProbeSummary != 0 ||
            Environment.TickCount64 - _trackerRoleProbeStartTick < 2000)
            return;
        _loggedTrackerRoleProbeSummary = 1;

        foreach (KeyValuePair<string, Space> entry in _trackerSpaces)
        {
            if (!_trackerSubactionPaths.TryGetValue(entry.Key, out XrPath subactionPath))
                continue;

            bool active = TryGetActivePoseState(_trackerPoseAction, subactionPath, out bool isActive) && isActive;
            bool bound = active || IsTrackerInteractionProfileBound(subactionPath);
            if (!active && !bound)
                continue;

            bool located = TryLocateSpace(entry.Value, displayTime, out Matrix4x4 pose, out bool posValid, out bool oriValid);
            Vector3 p = pose.Translation;
            Debug.WriteAuxiliaryLog(
                "openxr-trackers.log",
                $"TrackerRoleProbe role={entry.Key} active={active} bound={bound} located={located} posValid={posValid} oriValid={oriValid} pos=({p.X:F3},{p.Y:F3},{p.Z:F3})");
        }
    }

    private void ProbeUnenumeratedTrackerTransportRolesLocked(long displayTime)
    {
        if (Volatile.Read(ref _loggedActiveTransportRoleWithoutEnumeration) != 0)
            return;

        foreach (var entry in _trackerSpaces)
        {
            if (!_trackerSubactionPaths.TryGetValue(entry.Key, out XrPath subactionPath))
                continue;
            if (!TryGetActivePoseState(_trackerPoseAction, subactionPath, out bool active) || !active)
                continue;

            bool located = TryLocateSpace(entry.Value, displayTime, out _, out bool posValid, out bool oriValid);
            if (Interlocked.Exchange(ref _loggedActiveTransportRoleWithoutEnumeration, 1) == 0)
                Debug.Out($"OpenXR tracker transport probe: rolePath={entry.Key} is active while xrEnumerateViveTrackerPathsHTCX returned 0 paths (located={located}, posValid={posValid}, oriValid={oriValid}).");
            return;
        }
    }

    private void DestroyInput()
    {
        try
        {
            DestroyRuntimeNeutralInput();
            DestroyEyeGaze();

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
            _eyeGazeSpace = default;
            _eyeGazePoseAction = default;
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
            _trackerPersistentSubactionsAccepted = false;
            Volatile.Write(ref _loggedEmptyTrackerEnumerationAfterAttach, 0);
            Volatile.Write(ref _loggedActiveTransportRoleWithoutEnumeration, 0);
            Volatile.Write(ref _loggedPoseActionStateError, 0);
            _loggedDuplicateTrackerRoleConflicts.Clear();
            Volatile.Write(ref _lateTrackerRefreshRequired, 0);
        }
    }
}
