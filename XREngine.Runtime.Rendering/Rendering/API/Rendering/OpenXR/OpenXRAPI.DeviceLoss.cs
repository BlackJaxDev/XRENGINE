using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.EXT;
using System;
using System.Collections.Generic;
using System.Threading;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Terminal OpenXR parent abandonment following a Vulkan device loss.</summary>
public unsafe partial class OpenXRAPI
{
    private readonly object _deviceLossAbandonmentGate = new();
    private OpenXrDeviceLossAbandonmentSnapshot? _deviceLossAbandonment;
    private long _deviceLossQuiescingStartedTimestamp;

    internal void PrepareRendererDeviceLossAbandonment(
        AbstractRenderer renderer,
        string reason,
        OpenXrDeviceLossSource source)
    {
        lock (_deviceLossAbandonmentGate)
        {
            if (_deviceLossAbandonment is not null)
                return;

            long epoch = Volatile.Read(ref _smokeSessionLifecycleEpoch);
            _deviceLossAbandonment = new OpenXrDeviceLossAbandonmentSnapshot
            {
                Epoch = epoch,
                Source = source,
                SettlementState = OpenXrDeviceLossSettlementState.Quiescing,
                ManagedTerminal = true,
                NativeParentDisposition = OpenXrDeviceLossNativeParentDisposition.NotAttemptedNotQuiescent,
            };
            _deviceLossQuiescingStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _sessionBegun = false;
            _sessionState = SessionState.Unknown;
            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            Volatile.Write(ref _pendingXrFrameUsesTrueSinglePassStereo, 0);
            Volatile.Write(ref _framePrepared, 0);
            Volatile.Write(ref _frameSkipRender, 0);
            StopOpenXrPacingThread();

            if (_graphicsBinding is IXrGraphicsBinding binding && binding.IsCompatible(renderer))
            {
                OpenXrDeviceLossBindingAbandonment abandoned = binding.AbandonAfterDeviceLoss(this, renderer, reason);
                _deviceLossAbandonment.AbandonedGenerationCount = abandoned.AbandonedGenerationCount;
                _deviceLossAbandonment.AbandonedSwapchainCount = abandoned.AbandonedSwapchainCount;
                _deviceLossAbandonment.AbandonedAcquiredSwapchainCount = abandoned.AbandonedAcquiredSwapchainCount;
            }
            else
            {
                _deviceLossAbandonment.NativeParentDisposition = OpenXrDeviceLossNativeParentDisposition.StickyAbandoned;
                _deviceLossAbandonment.QuarantinedSessionHandle = _session.Handle;
                _deviceLossAbandonment.QuarantinedInstanceHandle = _instance.Handle;
                _deviceLossAbandonment.QuarantinedAppSpaceHandle = _appSpace.Handle;
                _deviceLossAbandonment.QuarantineReason = "No compatible attached graphics binding was available for terminal Vulkan device-loss abandonment.";
                CaptureUnattemptedDeviceLossChildOwnership(_deviceLossAbandonment);
                _deviceLossAbandonment.SettlementState = OpenXrDeviceLossSettlementState.StickyQuarantined;
                PublishDeviceLossAbandonment();
                return;
            }

            ClearManagedSwapchainOwnershipAfterDeviceLoss();
            PublishDeviceLossAbandonment();
        }

        ServiceRendererDeviceLossAbandonment(renderer, reason);
    }

    internal void ServiceRendererDeviceLossAbandonment(AbstractRenderer renderer, string reason)
    {
        IXrGraphicsBinding? binding = _graphicsBinding;
        lock (_deviceLossAbandonmentGate)
        {
            if (_deviceLossAbandonment is null || _deviceLossAbandonment.SettlementState is OpenXrDeviceLossSettlementState.Settled or OpenXrDeviceLossSettlementState.StickyQuarantined)
                return;
        }
        if (!RuntimeEngine.IsRenderThread && binding?.RequiresRenderThreadForTeardown == true)
        {
            RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(
                () => { ServiceRendererDeviceLossAbandonment(renderer, reason); return true; },
                $"OpenXR.{binding.BackendName}.DeviceLossAbandonment",
                RenderThreadJobKind.RequiresGraphicsContext);
            return;
        }
        lock (_deviceLossAbandonmentGate)
        {
            OpenXrDeviceLossAbandonmentSnapshot? snapshot = _deviceLossAbandonment;
            if (snapshot is null || snapshot.SettlementState is OpenXrDeviceLossSettlementState.Settled or OpenXrDeviceLossSettlementState.StickyQuarantined)
                return;

            StopOpenXrPacingThread();
            if (_openXrPacingThread is not null ||
                Volatile.Read(ref _openXrCollectVisiblePrepActive) != 0 ||
                Volatile.Read(ref _openXrFramePrepActive) != 0)
            {
                snapshot.SettlementState = OpenXrDeviceLossSettlementState.Quiescing;
                snapshot.NativeParentDisposition = OpenXrDeviceLossNativeParentDisposition.NotAttemptedNotQuiescent;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(_deviceLossQuiescingStartedTimestamp) >= TimeSpan.FromSeconds(1))
                {
                    snapshot.NativeParentDisposition = OpenXrDeviceLossNativeParentDisposition.StickyAbandoned;
                    snapshot.QuarantinedSessionHandle = _session.Handle;
                    snapshot.QuarantinedInstanceHandle = _instance.Handle;
                    snapshot.QuarantinedAppSpaceHandle = _appSpace.Handle;
                    CaptureUnattemptedDeviceLossChildOwnership(snapshot);
                    snapshot.QuarantineReason = "Pacing, CollectVisible preparation, or frame preparation did not quiesce within one second.";
                    snapshot.SettlementState = OpenXrDeviceLossSettlementState.StickyQuarantined;
                }
                PublishDeviceLossAbandonment();
                return;
            }

            snapshot.SettlementState = OpenXrDeviceLossSettlementState.Settling;
            DestroyDeviceLossChildren(snapshot);
            DestroyDeviceLossParents(renderer, reason, snapshot);
            snapshot.SettlementState = OpenXrDeviceLossSettlementState.Settled;
            PublishDeviceLossAbandonment();
        }
    }

    private void ClearManagedSwapchainOwnershipAfterDeviceLoss()
    {
        InvalidateOpenXrViewHistory();
        for (int i = 0; i < _swapchains.Length; ++i)
        {
            _swapchains[i] = default;
            _swapchainImageCounts[i] = 0;
            _swapchainWidths[i] = 0;
            _swapchainHeights[i] = 0;
        }
        _viewCount = 0;
    }

    private void DestroyDeviceLossChildren(OpenXrDeviceLossAbandonmentSnapshot snapshot)
    {
        if (Api is null)
            return;
        DestroyDeviceLossInputChildren(snapshot);
        if (_appSpace.Handle == 0)
            return;
        snapshot.ChildDestroyAttempted++;
        try
        {
            Result result = Api.DestroySpace(_appSpace);
            if (result is Result.Success or Result.ErrorInstanceLost) snapshot.ChildDestroySucceeded++;
            else { snapshot.ChildDestroyFailed++; snapshot.QuarantinedAppSpaceHandle = _appSpace.Handle; }
        }
        catch { snapshot.ChildDestroyFailed++; snapshot.QuarantinedAppSpaceHandle = _appSpace.Handle; }
        _appSpace = default;
    }

    private void DestroyDeviceLossParents(AbstractRenderer renderer, string reason, OpenXrDeviceLossAbandonmentSnapshot snapshot)
    {
        bool parentFailure = false;
        ulong rendererOwnedInstanceHandle = 0;
        if (_instanceOwnedByRenderer &&
            _graphicsBinding is IXrGraphicsBinding attachedBinding &&
            attachedBinding.IsCompatible(renderer) &&
            attachedBinding.TryGetRendererOwnedInstance(renderer, out _, out Instance rendererOwnedInstance, out _))
        {
            rendererOwnedInstanceHandle = rendererOwnedInstance.Handle;
        }
        if (_session.Handle != 0)
        {
            snapshot.SessionDestroyAttempted = true;
            try
            {
                snapshot.SessionDestroyResult = Api is null
                    ? Result.ErrorHandleInvalid
                    : Api.DestroySession(_session);
                parentFailure |= snapshot.SessionDestroyResult is not (Result.Success or Result.ErrorInstanceLost);
            }
            catch (Exception ex) { snapshot.SessionDestroyExceptionCategory = ex.GetType().Name; parentFailure = true; }
            if (snapshot.SessionDestroyResult is Result.Success or Result.ErrorInstanceLost)
                _session = default;
            else
            {
                snapshot.QuarantinedSessionHandle = _session.Handle;
                _session = default;
            }
        }

        if (_instance.Handle != 0 || _instanceOwnedByRenderer)
        {
            snapshot.InstanceDestroyAttempted = true;
            try
            {
                if (_instanceOwnedByRenderer &&
                    _graphicsBinding is IXrGraphicsBinding binding && binding.IsCompatible(renderer))
                {
                    snapshot.InstanceDestroyResult = binding.TryDestroyRendererOwnedInstanceAfterDeviceLoss(renderer, reason);
                    if (snapshot.InstanceDestroyResult is not (Result.Success or Result.ErrorInstanceLost))
                        snapshot.QuarantinedInstanceHandle = rendererOwnedInstanceHandle;
                }
                else
                    snapshot.InstanceDestroyResult = Api is null || _instance.Handle == 0
                        ? Result.ErrorHandleInvalid
                        : Api.DestroyInstance(_instance);
                parentFailure |= snapshot.InstanceDestroyResult is not (Result.Success or Result.ErrorInstanceLost);
            }
            catch (Exception ex) { snapshot.InstanceDestroyExceptionCategory = ex.GetType().Name; parentFailure = true; }
            if (snapshot.InstanceDestroyResult is Result.Success or Result.ErrorInstanceLost)
                _instance = default;
            else
            {
                if (snapshot.QuarantinedInstanceHandle == 0)
                    snapshot.QuarantinedInstanceHandle = _instance.Handle;
                _instance = default;
            }
        }

        _instanceOwnedByRenderer = false;
        _apiOwnedByRenderer = false;
        _systemId = 0;
        ClearInstanceExtensionState();
        snapshot.NativeParentDisposition = parentFailure
            ? OpenXrDeviceLossNativeParentDisposition.StickyAbandoned
            : snapshot.InstanceDestroyResult == Result.ErrorInstanceLost || snapshot.SessionDestroyResult == Result.ErrorInstanceLost
                ? OpenXrDeviceLossNativeParentDisposition.AlreadyInvalid
                : OpenXrDeviceLossNativeParentDisposition.Destroyed;
    }

    private void DestroyDeviceLossInputChildren(OpenXrDeviceLossAbandonmentSnapshot snapshot)
    {
        List<string> quarantined = [];
        DestroyDeviceLossSpace("left-grip-space", _leftHandGripSpace, ref _leftHandGripSpace, snapshot, quarantined);
        DestroyDeviceLossSpace("right-grip-space", _rightHandGripSpace, ref _rightHandGripSpace, snapshot, quarantined);
        DestroyDeviceLossSpace("left-aim-space", _leftHandAimSpace, ref _leftHandAimSpace, snapshot, quarantined);
        DestroyDeviceLossSpace("right-aim-space", _rightHandAimSpace, ref _rightHandAimSpace, snapshot, quarantined);
        foreach ((string key, Space space) in _trackerSpaces)
        {
            Space mutableSpace = space;
            DestroyDeviceLossSpace($"tracker-space:{key}", mutableSpace, ref mutableSpace, snapshot, quarantined);
        }
        _trackerSpaces.Clear();
        DestroyDeviceLossAction("hand-grip-action", _handGripPoseAction, ref _handGripPoseAction, snapshot, quarantined);
        DestroyDeviceLossAction("tracker-pose-action", _trackerPoseAction, ref _trackerPoseAction, snapshot, quarantined);
        DestroyDeviceLossAction("hand-aim-action", _handAimPoseAction, ref _handAimPoseAction, snapshot, quarantined);
        DestroyDeviceLossAction("haptic-action", _hapticAction, ref _hapticAction, snapshot, quarantined);
        DestroyDeviceLossHandTracker("left-hand-tracker", _leftHandTracker, ref _leftHandTracker, snapshot, quarantined);
        DestroyDeviceLossHandTracker("right-hand-tracker", _rightHandTracker, ref _rightHandTracker, snapshot, quarantined);
        for (int i = 0; i < _runtimeInputActionList.Count; ++i)
        {
            Silk.NET.OpenXR.Action action = _runtimeInputActionList[i].Action;
            DestroyDeviceLossAction($"runtime-action:{i}", action, ref action, snapshot, quarantined);
        }
        _runtimeInputActions.Clear();
        _runtimeInputActionList.Clear();
        DestroyDeviceLossActionSet("input-action-set", _inputActionSet, ref _inputActionSet, snapshot, quarantined);
        snapshot.QuarantinedChildHandles = [.. quarantined];
    }

    private void DestroyDeviceLossSpace(string name, Space handle, ref Space storage, OpenXrDeviceLossAbandonmentSnapshot snapshot, List<string> quarantined)
    {
        if (handle.Handle == 0) return;
        snapshot.ChildDestroyAttempted++;
        try
        {
            Result result = Api.DestroySpace(handle);
            if (result is Result.Success or Result.ErrorInstanceLost) snapshot.ChildDestroySucceeded++; else { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({result})"); }
        }
        catch (Exception ex) { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({ex.GetType().Name})"); }
        storage = default;
    }

    private void DestroyDeviceLossAction(string name, Silk.NET.OpenXR.Action handle, ref Silk.NET.OpenXR.Action storage, OpenXrDeviceLossAbandonmentSnapshot snapshot, List<string> quarantined)
    {
        if (handle.Handle == 0) return;
        snapshot.ChildDestroyAttempted++;
        try
        {
            Result result = Api.DestroyAction(handle);
            if (result is Result.Success or Result.ErrorInstanceLost) snapshot.ChildDestroySucceeded++; else { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({result})"); }
        }
        catch (Exception ex) { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({ex.GetType().Name})"); }
        storage = default;
    }

    private void DestroyDeviceLossActionSet(string name, ActionSet handle, ref ActionSet storage, OpenXrDeviceLossAbandonmentSnapshot snapshot, List<string> quarantined)
    {
        if (handle.Handle == 0) return;
        snapshot.ChildDestroyAttempted++;
        try
        {
            Result result = Api.DestroyActionSet(handle);
            if (result is Result.Success or Result.ErrorInstanceLost) snapshot.ChildDestroySucceeded++; else { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({result})"); }
        }
        catch (Exception ex) { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({ex.GetType().Name})"); }
        storage = default;
    }

    private void DestroyDeviceLossHandTracker(string name, HandTrackerEXT handle, ref HandTrackerEXT storage, OpenXrDeviceLossAbandonmentSnapshot snapshot, List<string> quarantined)
    {
        if (handle.Handle == 0) return;
        snapshot.ChildDestroyAttempted++;
        try
        {
            Result result = _handTracking is null ? Result.ErrorHandleInvalid : _handTracking.DestroyHandTracker(handle);
            if (result is Result.Success or Result.ErrorInstanceLost) snapshot.ChildDestroySucceeded++; else { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({result})"); }
        }
        catch (Exception ex) { snapshot.ChildDestroyFailed++; AddQuarantinedHandle(quarantined, $"{name}=0x{handle.Handle:X} ({ex.GetType().Name})"); }
        storage = default;
    }

    private void CaptureUnattemptedDeviceLossChildOwnership(OpenXrDeviceLossAbandonmentSnapshot snapshot)
    {
        List<string> handles = [];
        void Add(string name, ulong handle) { if (handle != 0) AddQuarantinedHandle(handles, $"{name}=0x{handle:X}"); }
        Add("left-grip-space", _leftHandGripSpace.Handle); Add("right-grip-space", _rightHandGripSpace.Handle);
        Add("left-aim-space", _leftHandAimSpace.Handle); Add("right-aim-space", _rightHandAimSpace.Handle);
        Add("hand-grip-action", _handGripPoseAction.Handle); Add("tracker-pose-action", _trackerPoseAction.Handle);
        Add("hand-aim-action", _handAimPoseAction.Handle); Add("haptic-action", _hapticAction.Handle); Add("input-action-set", _inputActionSet.Handle);
        Add("left-hand-tracker", _leftHandTracker.Handle); Add("right-hand-tracker", _rightHandTracker.Handle);
        foreach ((string key, Space space) in _trackerSpaces) Add($"tracker-space:{key}", space.Handle);
        for (int i = 0; i < _runtimeInputActionList.Count; ++i) Add($"runtime-action:{i}", _runtimeInputActionList[i].Action.Handle);
        snapshot.QuarantinedChildHandles = [.. handles];
    }

    private static void AddQuarantinedHandle(List<string> handles, string value)
    {
        if (handles.Count < 64)
            handles.Add(value);
    }

    private void PublishDeviceLossAbandonment()
    {
        OpenXrDeviceLossAbandonmentSnapshot snapshot = _deviceLossAbandonment!;
        _smokeDeviceLossAbandonment = CloneDeviceLossAbandonment(snapshot);
        Volatile.Write(ref _smokeLastDeviceLossAbandonmentEpoch, snapshot.Epoch);
        if (Volatile.Read(ref _smokeDeviceLossAbandonmentCount) == 0)
            Interlocked.Increment(ref _smokeDeviceLossAbandonmentCount);
    }

    private static OpenXrDeviceLossAbandonmentSnapshot CloneDeviceLossAbandonment(OpenXrDeviceLossAbandonmentSnapshot source)
        => new()
        {
            Epoch = source.Epoch, Source = source.Source, SettlementState = source.SettlementState,
            ManagedTerminal = source.ManagedTerminal, AbandonedGenerationCount = source.AbandonedGenerationCount,
            AbandonedSwapchainCount = source.AbandonedSwapchainCount, AbandonedAcquiredSwapchainCount = source.AbandonedAcquiredSwapchainCount,
            ChildDestroyAttempted = source.ChildDestroyAttempted, ChildDestroySucceeded = source.ChildDestroySucceeded,
            ChildDestroyFailed = source.ChildDestroyFailed, SessionDestroyAttempted = source.SessionDestroyAttempted,
            SessionDestroyResult = source.SessionDestroyResult, SessionDestroyExceptionCategory = source.SessionDestroyExceptionCategory,
            QuarantinedSessionHandle = source.QuarantinedSessionHandle, InstanceDestroyAttempted = source.InstanceDestroyAttempted,
            InstanceDestroyResult = source.InstanceDestroyResult, InstanceDestroyExceptionCategory = source.InstanceDestroyExceptionCategory,
            QuarantinedInstanceHandle = source.QuarantinedInstanceHandle, QuarantinedAppSpaceHandle = source.QuarantinedAppSpaceHandle,
            QuarantinedChildHandles = [.. source.QuarantinedChildHandles], QuarantineReason = source.QuarantineReason,
            NativeParentDisposition = source.NativeParentDisposition,
        };
}
