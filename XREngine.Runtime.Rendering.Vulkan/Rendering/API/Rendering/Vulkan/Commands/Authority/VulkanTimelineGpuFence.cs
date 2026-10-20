using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned timeline completion receipt with no renderer backlink.</summary>
internal sealed class VulkanTimelineGpuFence : XRGpuFence
{
    private Vk? _api;
    private VulkanDeviceContext? _device;
    private VulkanCommandRuntime? _runtime;
    private VulkanResourceRuntime? _resources;
    private ulong _semaphore;
    private ulong _value;
    private int _state;
    private int _disposePendingBackendResolution;
    private long _rentalId;
    private ulong _authoredFrame;
    private int _authoredPass;
    private ulong _commandBuffer;
    private GpuFenceFailureDiagnostic? _firstFailure;

    public override long DiagnosticRentalId => _rentalId;
    public override GpuFenceFailureDiagnostic? FirstFailureDiagnostic
    {
        get
        {
            VulkanCommandRuntime? runtime = _runtime;
            if (runtime is null)
                return _firstFailure;
            lock (runtime.Synchronization._submissionMarkerLock)
                return _firstFailure;
        }
    }

    public override EGpuFenceSubmissionStatus SubmissionStatus
        => Volatile.Read(ref _state) switch
        {
            1 => EGpuFenceSubmissionStatus.Submitted,
            2 => EGpuFenceSubmissionStatus.Failed,
            _ => EGpuFenceSubmissionStatus.AwaitingSubmission,
        };

    internal void Reset(Vk api, VulkanDeviceContext device, VulkanCommandRuntime runtime, VulkanResourceRuntime resources)
    {
        ResetForReuse();
        _api = api; _device = device; _runtime = runtime; _resources = resources;
        _semaphore = 0; _value = 0;
        _rentalId = runtime.Synchronization.NextFenceRentalIdNoLock();
        _authoredFrame = 0;
        _authoredPass = 0;
        _commandBuffer = 0;
        _firstFailure = null;
        Volatile.Write(ref _disposePendingBackendResolution, 0);
        Volatile.Write(ref _state, 0);
    }
    internal void SetAuthoringContext(ulong frame, int pass)
    {
        _authoredFrame = frame;
        _authoredPass = pass;
    }

    internal void SetCommandBuffer(ulong handle) => _commandBuffer = handle;
    internal void Bind(ulong semaphore, ulong value)
    {
        VulkanCommandRuntime? runtime = _runtime;
        if (runtime is null)
        {
            Volatile.Write(ref _state, 2);
            return;
        }

        lock (runtime.Synchronization._submissionMarkerLock)
        {
            if (Volatile.Read(ref _disposePendingBackendResolution) != 0)
            {
                ReturnToPoolNoLock(runtime);
                return;
            }

            if (semaphore == 0 || value == 0)
            {
                RecordFirstFailureNoLock(runtime, EGpuFenceFailureSite.MissingTimelineSignal,
                    EGpuFenceNativeSubmission.Accepted, 0);
                Volatile.Write(ref _state, 2);
                return;
            }

            _semaphore = semaphore;
            _value = value;
            Volatile.Write(ref _state, 1);
        }
    }
    internal void Fail(EGpuFenceFailureSite site = EGpuFenceFailureSite.Unknown,
        EGpuFenceNativeSubmission nativeSubmission = EGpuFenceNativeSubmission.Unknown,
        int nativeResult = 0)
    {
        VulkanCommandRuntime? runtime = _runtime;
        if (runtime is null)
        {
            Volatile.Write(ref _state, 2);
            return;
        }

        lock (runtime.Synchronization._submissionMarkerLock)
        {
            RecordFirstFailureNoLock(runtime, site, nativeSubmission, nativeResult);
            if (Volatile.Read(ref _disposePendingBackendResolution) != 0)
            {
                ReturnToPoolNoLock(runtime);
                return;
            }

            Volatile.Write(ref _state, 2);
        }
    }
    private void RecordFirstFailureNoLock(VulkanCommandRuntime runtime,
        EGpuFenceFailureSite site, EGpuFenceNativeSubmission nativeSubmission, int nativeResult)
    {
        if (_firstFailure.HasValue)
            return;
        GpuFenceFailureDiagnostic failure = new(
            _rentalId, _authoredFrame, _authoredPass,
            RuntimeEngine.Rendering.State.RenderFrameId, site, SubmissionStatus,
            _commandBuffer, nativeSubmission, nativeResult,
            site is EGpuFenceFailureSite.NativeSubmitFailed or EGpuFenceFailureSite.TimelineQuery,
            _semaphore, _value);
        _firstFailure = failure;
        runtime.Synchronization.RecordFenceFailureNoLock(in failure);
    }
    protected override unsafe EGpuFenceStatus PollCore()
    {
        if (Volatile.Read(ref _state) == 2) return EGpuFenceStatus.Failed;
        if (_api is not { } api || _device is not { } device || _runtime is not { } runtime || _resources is not { } resources || !device.StateMachine.IsOperational)
        { Fail(EGpuFenceFailureSite.BackendUnavailable); return EGpuFenceStatus.Failed; }
        if (Volatile.Read(ref _state) == 0 || _semaphore == 0 || _value == 0) return EGpuFenceStatus.Pending;
        Result result = runtime.Synchronization.QueryTimelineCompletion(api, device, resources.Lifetime.Tracker, new Semaphore(_semaphore), _value, out bool completed);
        if (result != Result.Success) { Fail(EGpuFenceFailureSite.TimelineQuery,
            EGpuFenceNativeSubmission.Accepted, (int)result); return EGpuFenceStatus.Failed; }
        return completed ? EGpuFenceStatus.Signaled : EGpuFenceStatus.Pending;
    }
    protected override void DisposeCore()
    {
        VulkanCommandRuntime? runtime = _runtime;
        if (runtime is null)
        {
            _api = null; _device = null; _resources = null; _semaphore = 0; _value = 0;
            Volatile.Write(ref _state, 2);
            return;
        }

        lock (runtime.Synchronization._submissionMarkerLock)
        {
            bool awaitingBackendResolution = Volatile.Read(ref _state) == 0;
            _api = null; _device = null; _resources = null; _semaphore = 0; _value = 0;
            Volatile.Write(ref _state, 2);
            if (awaitingBackendResolution)
            {
                // The command stream still owns a reference to this marker. Keep
                // the runtime backlink until Bind/Fail releases that reference,
                // then return the object to the pool without making it reusable
                // while the backend can still touch it.
                Volatile.Write(ref _disposePendingBackendResolution, 1);
                return;
            }

            ReturnToPoolNoLock(runtime);
        }
    }

    private void ReturnToPoolNoLock(VulkanCommandRuntime runtime)
    {
        _api = null; _device = null; _runtime = null; _resources = null; _semaphore = 0; _value = 0;
        Volatile.Write(ref _state, 2);
        Volatile.Write(ref _disposePendingBackendResolution, 0);
        runtime.Synchronization._timelineGpuFencePool.Push(this);
    }
}
