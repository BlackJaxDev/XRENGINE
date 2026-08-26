using System.Threading;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded, allocation-free handoff for immutable mesh operation requests.
/// The queue deliberately retains no renderer authority; its consumer supplies
/// planning, output, command, resource, and telemetry state at drain time.
/// </summary>
internal sealed class VulkanMeshOperationRequestQueue
{
    internal enum EMeshRequestScheduleResult
    {
        Scheduled,
        AlreadyReady,
        Backpressured,
        TerminalFailure,
    }
    // A frame can contain a full shadow-caster cohort followed by the main-view
    // and composition draws.  Keeping only one 1K cohort allowed the shadow
    // pass to consume the entire queue, dropping the later fullscreen present
    // draw while the camera was moving.  Four cohorts cover the current
    // directional-cascade workload without allocating in the render hot path.
    /// <summary>
    /// Declared immutable frame-manifest capacity. This is not the background
    /// scheduling slice and cannot be tuned to hide readiness bugs.
    /// </summary>
    internal const int Capacity = 4096;
    internal const int TerminalCompositionCapacity = 256;
    internal const int UiCapacity = 256;
    internal const int MainSceneCapacity = 1536;
    internal const int ShadowCapacity = 2048;
    private const int PublicationLeaseCapacity = Capacity;
    private static readonly int BackgroundSchedulingCapacity =
        ResolveBackgroundSchedulingCapacity();
    private readonly VulkanMeshRenderRequest[] _entries = new VulkanMeshRenderRequest[Capacity];
    private readonly EVulkanMeshRequestLane[] _lanes = new EVulkanMeshRequestLane[Capacity];
    private readonly object _gate = new();
    private readonly ThreadLocal<ThreadCaptureState> _threadCapture =
        new(static () => new ThreadCaptureState(), trackAllValues: true);
    private readonly CanonicalPublicationLeaseBatch _publishedPublicationLeases = new();
    private readonly CanonicalPublicationLeaseBatch _drainedPublicationLeases = new();
    private int _head;
    private int _count;
    private int _framePlanCapacityExceededCount;
    private int _terminalCompositionCount;
    private int _uiCount;
    private int _mainSceneCount;
    private int _shadowCount;
    private VulkanMeshRequestLaneCapacityFailure _lastCapacityFailure;
    internal EMeshRequestScheduleResult TryEnqueue(in VulkanMeshRenderRequest request)
    {
        ThreadCaptureState capture = _threadCapture.Value
            ?? throw new InvalidOperationException(
                "The Vulkan mesh-operation request queue capture state is unavailable.");
        if (capture.Destination is { } destination)
        {
            if (capture.Failed || capture.Count == destination.Length)
            {
                capture.Failed = true;
                return EMeshRequestScheduleResult.Backpressured;
            }
            if (!capture.TryRetainPublication(
                    request.CanonicalDrawIdentitySnapshot))
            {
                capture.Failed = true;
                return EMeshRequestScheduleResult.TerminalFailure;
            }

            destination[capture.Count++] = request;
            return EMeshRequestScheduleResult.Scheduled;
        }

        lock (_gate)
        {
            EVulkanMeshRequestLane lane = ResolveLane(in request);
            int occupancy = GetLaneOccupancy(lane);
            int laneCapacity = GetLaneCapacity(lane);
            if (occupancy >= laneCapacity)
            {
                RecordCapacityFailure(lane, laneCapacity, occupancy);
                return EMeshRequestScheduleResult.TerminalFailure;
            }

            VulkanMeshRenderRequest acceptedRequest = request;
            if (!_publishedPublicationLeases.TryRetain(
                    request.CanonicalDrawIdentitySnapshot))
            {
                // The canonical publication bridge is an optimization. Preserve
                // the immutable draw through the legacy materialization path when
                // its optional pin cannot be retained; never poison or drop it.
                acceptedRequest = request with
                {
                    CanonicalDrawIdentitySnapshot = default,
                    ResidentTemplateHandle = default,
                };
            }

            int tail = _head + _count;
            if (tail >= Capacity)
                tail -= Capacity;
            _entries[tail] = acceptedRequest;
            _lanes[tail] = lane;
            _count++;
            IncrementLaneOccupancy(lane);
            return EMeshRequestScheduleResult.Scheduled;
        }
    }

    /// <summary>
    /// Captures requests emitted by <paramref name="emitRequests"/> on the calling
    /// thread directly into caller-owned storage. Other producer threads continue
    /// publishing to the shared queue.
    /// </summary>
    internal int CaptureTo(
        Action emitRequests,
        VulkanMeshRenderRequest[] destination)
    {
        ArgumentNullException.ThrowIfNull(emitRequests);
        ArgumentNullException.ThrowIfNull(destination);

        ThreadCaptureState capture = BeginThreadCapture(destination);
        bool completed = false;
        try
        {
            emitRequests();
            completed = !capture.Failed;
            return completed ? capture.Count : -1;
        }
        finally
        {
            EndThreadCapture(capture, clearCapturedRequests: !completed);
        }
    }

    /// <summary>
    /// Captures one allocation-free OpenXR eye emission into caller-owned storage.
    /// </summary>
    internal int CaptureTo(
        IOpenXrEyeFrameOpEmitter emitter,
        in OpenXrEyeFrameOpEmission emission,
        VulkanMeshRenderRequest[] destination)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(destination);

        ThreadCaptureState capture = BeginThreadCapture(destination);
        bool completed = false;
        try
        {
            emitter.Emit(in emission);
            completed = !capture.Failed;
            return completed ? capture.Count : -1;
        }
        finally
        {
            EndThreadCapture(capture, clearCapturedRequests: !completed);
        }
    }

    private ThreadCaptureState BeginThreadCapture(
        VulkanMeshRenderRequest[] destination)
    {
        ThreadCaptureState capture = _threadCapture.Value
            ?? throw new InvalidOperationException(
                "The Vulkan mesh-operation request queue capture state is unavailable.");
        if (capture.Destination is not null)
        {
            throw new InvalidOperationException(
                "Nested Vulkan mesh-operation request captures are not supported on the same thread.");
        }

        capture.Destination = destination;
        capture.Count = 0;
        capture.Failed = false;
        capture.AdvancePublicationLeaseBatch();
        return capture;
    }

    private static void EndThreadCapture(
        ThreadCaptureState capture,
        bool clearCapturedRequests)
    {
        VulkanMeshRenderRequest[]? destination = capture.Destination;
        int count = capture.Count;
        capture.Destination = null;
        capture.Count = 0;
        capture.Failed = false;
        if (clearCapturedRequests && destination is not null && count > 0)
        {
            destination.AsSpan(0, count).Clear();
            capture.ReleaseCurrentPublicationLeases();
        }
    }

    internal bool TryDequeue(out VulkanMeshRenderRequest request)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _entries[_head];
            EVulkanMeshRequestLane lane = _lanes[_head];
            _entries[_head] = default;
            _lanes[_head] = default;
            _head++;
            if (_head == Capacity)
                _head = 0;
            _count--;
            DecrementLaneOccupancy(lane);
            return true;
        }
    }

    /// <summary>
    /// Moves the currently published request cohort into caller-owned storage
    /// under one short queue lock. New producers may begin publishing the next
    /// cohort as soon as the copy completes; Vulkan resource preparation never
    /// runs while the handoff gate is held.
    /// </summary>
    internal int DrainTo(Span<VulkanMeshRenderRequest> destination)
        => DrainTo(
            destination,
            foregroundRequired: false,
            out _,
            out _);

    /// <summary>
    /// Drains one scheduling slice without changing the accepted manifest.
    /// Foreground drains ignore the background slice cap; both remain bounded by
    /// the declared arena and report exact overflow instead of truncating.
    /// </summary>
    internal int DrainTo(
        Span<VulkanMeshRenderRequest> destination,
        bool foregroundRequired,
        out int acceptedRequestCount,
        out int capacityExceededCount)
        => DrainTo(
            destination,
            foregroundRequired,
            out acceptedRequestCount,
            out capacityExceededCount,
            out _);

    /// <summary>
    /// Drains a lane-isolated manifest cohort. Background policy may limit a
    /// scheduling slice, but foreground never bypasses a real lane capacity.
    /// </summary>
    internal int DrainTo(
        Span<VulkanMeshRenderRequest> destination,
        bool foregroundRequired,
        out int acceptedRequestCount,
        out int capacityExceededCount,
        out VulkanMeshRequestLaneCapacityFailure capacityFailure)
    {
        lock (_gate)
        {
            acceptedRequestCount = _count;
            capacityExceededCount = _framePlanCapacityExceededCount;
            capacityFailure = _lastCapacityFailure;
            _framePlanCapacityExceededCount = 0;
            _lastCapacityFailure = default;
            if (destination.Length == 0 || _count == 0)
                return 0;

            int schedulingLimit = foregroundRequired
                ? destination.Length
                : Math.Min(destination.Length, BackgroundSchedulingCapacity);
            int drainCount = Math.Min(_count, schedulingLimit);
            // The lease batch is aggregate rather than per-entry. Transfer it only
            // when the complete queue is drained; partial drains leave the leases
            // published until the remaining entries are handed off.
            if (drainCount == _count)
            {
                _drainedPublicationLeases.ReleaseAll();
                _publishedPublicationLeases.MoveTo(_drainedPublicationLeases);
            }
            int firstCount = Math.Min(drainCount, Capacity - _head);
            _entries.AsSpan(_head, firstCount).CopyTo(destination);
            DecrementLaneOccupancies(_lanes.AsSpan(_head, firstCount));
            _entries.AsSpan(_head, firstCount).Clear();
            _lanes.AsSpan(_head, firstCount).Clear();

            int secondCount = drainCount - firstCount;
            if (secondCount > 0)
            {
                _entries.AsSpan(0, secondCount).CopyTo(destination[firstCount..]);
                DecrementLaneOccupancies(_lanes.AsSpan(0, secondCount));
                _entries.AsSpan(0, secondCount).Clear();
                _lanes.AsSpan(0, secondCount).Clear();
            }

            _head += drainCount;
            if (_head >= Capacity)
                _head -= Capacity;
            _count -= drainCount;
            return drainCount;
        }
    }

    private static int ResolveBackgroundSchedulingCapacity()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.VulkanMeshSchedulingCapacity);
        return int.TryParse(configured, out int parsed)
            ? Math.Clamp(parsed, 1, Capacity)
            : Capacity;
    }

    private static EVulkanMeshRequestLane ResolveLane(in VulkanMeshRenderRequest request)
        => request.Context.ContextKind switch
        {
            EVulkanFrameOpContextKind.Shadow => EVulkanMeshRequestLane.Shadow,
            EVulkanFrameOpContextKind.UiPreview => EVulkanMeshRequestLane.Ui,
            EVulkanFrameOpContextKind.MainViewport or EVulkanFrameOpContextKind.OpenXrEye => EVulkanMeshRequestLane.MainScene,
            _ => EVulkanMeshRequestLane.TerminalComposition,
        };

    private static int GetLaneCapacity(EVulkanMeshRequestLane lane)
        => lane switch
        {
            EVulkanMeshRequestLane.TerminalComposition => TerminalCompositionCapacity,
            EVulkanMeshRequestLane.Ui => UiCapacity,
            EVulkanMeshRequestLane.MainScene => MainSceneCapacity,
            EVulkanMeshRequestLane.Shadow => ShadowCapacity,
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };

    private int GetLaneOccupancy(EVulkanMeshRequestLane lane)
        => lane switch
        {
            EVulkanMeshRequestLane.TerminalComposition => _terminalCompositionCount,
            EVulkanMeshRequestLane.Ui => _uiCount,
            EVulkanMeshRequestLane.MainScene => _mainSceneCount,
            EVulkanMeshRequestLane.Shadow => _shadowCount,
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };

    private void IncrementLaneOccupancy(EVulkanMeshRequestLane lane)
    {
        switch (lane)
        {
            case EVulkanMeshRequestLane.TerminalComposition: _terminalCompositionCount++; break;
            case EVulkanMeshRequestLane.Ui: _uiCount++; break;
            case EVulkanMeshRequestLane.MainScene: _mainSceneCount++; break;
            case EVulkanMeshRequestLane.Shadow: _shadowCount++; break;
            default: throw new ArgumentOutOfRangeException(nameof(lane));
        }
    }

    private void DecrementLaneOccupancy(EVulkanMeshRequestLane lane)
    {
        switch (lane)
        {
            case EVulkanMeshRequestLane.TerminalComposition: _terminalCompositionCount--; break;
            case EVulkanMeshRequestLane.Ui: _uiCount--; break;
            case EVulkanMeshRequestLane.MainScene: _mainSceneCount--; break;
            case EVulkanMeshRequestLane.Shadow: _shadowCount--; break;
            default: throw new ArgumentOutOfRangeException(nameof(lane));
        }
    }

    private void DecrementLaneOccupancies(ReadOnlySpan<EVulkanMeshRequestLane> lanes)
    {
        for (int index = 0; index < lanes.Length; index++)
            DecrementLaneOccupancy(lanes[index]);
    }

    private void RecordCapacityFailure(
        EVulkanMeshRequestLane lane,
        int configuredCapacity,
        int actualOccupancy)
    {
        int overflowCount = ++_framePlanCapacityExceededCount;
        _lastCapacityFailure = new VulkanMeshRequestLaneCapacityFailure(
            lane,
            configuredCapacity,
            actualOccupancy,
            actualOccupancy + 1,
            overflowCount);
    }

    internal void ReleaseCanonicalPublicationLeases()
    {
        lock (_gate)
        {
            _publishedPublicationLeases.ReleaseAll();
            _drainedPublicationLeases.ReleaseAll();
        }

        foreach (ThreadCaptureState capture in _threadCapture.Values)
            capture.ReleasePublicationLeases();
    }

    internal void ReleaseCanonicalPublicationBridge(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw)
    {
        if (!canonicalDraw.IsValid)
            return;

        lock (_gate)
        {
            _publishedPublicationLeases.ReleaseMatching(canonicalDraw);
            _drainedPublicationLeases.ReleaseMatching(canonicalDraw);
        }
    }

    private sealed class ThreadCaptureState
    {
        private readonly object _leaseGate = new();
        internal VulkanMeshRenderRequest[]? Destination;
        internal int Count;
        internal bool Failed;
        private CanonicalPublicationLeaseBatch PublicationLeases { get; } = new();
        private CanonicalPublicationLeaseBatch PreviousPublicationLeases { get; } = new();

        internal bool TryRetainPublication(
            in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw)
        {
            lock (_leaseGate)
                return PublicationLeases.TryRetain(canonicalDraw);
        }

        internal void AdvancePublicationLeaseBatch()
        {
            lock (_leaseGate)
            {
                PreviousPublicationLeases.ReleaseAll();
                PublicationLeases.MoveTo(PreviousPublicationLeases);
            }
        }

        internal void ReleasePublicationLeases()
        {
            lock (_leaseGate)
            {
                PublicationLeases.ReleaseAll();
                PreviousPublicationLeases.ReleaseAll();
            }
        }

        internal void ReleaseCurrentPublicationLeases()
        {
            lock (_leaseGate)
                PublicationLeases.ReleaseAll();
        }


    }

    /// <summary>
    /// Bounded bridge between package projection and frame-slot acquisition.
    /// The bridge owns one GPU pin per distinct publication, never per draw.
    /// </summary>
    private sealed class CanonicalPublicationLeaseBatch
    {
        private readonly AdvancedSharedGpuSceneDatabase?[] _databases =
            new AdvancedSharedGpuSceneDatabase[PublicationLeaseCapacity];
        private readonly AdvancedGpuScenePublicationReference[] _publications =
            new AdvancedGpuScenePublicationReference[PublicationLeaseCapacity];
        private readonly AdvancedGpuScenePublicationLease[] _leases =
            new AdvancedGpuScenePublicationLease[PublicationLeaseCapacity];
        private int _count;

        internal bool TryRetain(
            in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw)
        {
            if (!canonicalDraw.IsValid || canonicalDraw.Database is not { } database)
                return true;

            AdvancedGpuScenePublicationReference publication =
                canonicalDraw.Publication;
            for (int index = 0; index < _count; ++index)
            {
                if (ReferenceEquals(_databases[index], database) &&
                    _publications[index] == publication)
                {
                    return true;
                }
            }
            if (_count == _leases.Length ||
                !database.TryAcquirePublicationLease(
                    publication,
                    EAdvancedGpuScenePublicationPinKind.Gpu,
                    out AdvancedGpuScenePublicationLease lease))
            {
                return false;
            }

            int slot = _count++;
            _databases[slot] = database;
            _publications[slot] = publication;
            _leases[slot] = lease;
            return true;
        }

        internal void MoveTo(CanonicalPublicationLeaseBatch destination)
        {
            if (destination._count != 0)
                throw new InvalidOperationException(
                    "Canonical publication lease destination was not retired before transfer.");

            for (int index = 0; index < _count; ++index)
            {
                destination._databases[index] = _databases[index];
                destination._publications[index] = _publications[index];
                destination._leases[index] = _leases[index];
                _databases[index] = null;
                _publications[index] = default;
                _leases[index] = default;
            }
            destination._count = _count;
            _count = 0;
        }

        internal void ReleaseAll()
        {
            for (int index = 0; index < _count; ++index)
            {
                _leases[index].Dispose();
                _databases[index] = null;
                _publications[index] = default;
                _leases[index] = default;
            }
            _count = 0;
        }

        internal void ReleaseMatching(
            in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw)
        {
            AdvancedSharedGpuSceneDatabase? database = canonicalDraw.Database;
            AdvancedGpuScenePublicationReference publication =
                canonicalDraw.Publication;
            for (int index = 0; index < _count; ++index)
            {
                if (!ReferenceEquals(_databases[index], database) ||
                    _publications[index] != publication)
                {
                    continue;
                }

                _leases[index].Dispose();
                int last = --_count;
                if (index != last)
                {
                    _databases[index] = _databases[last];
                    _publications[index] = _publications[last];
                    _leases[index] = _leases[last];
                }
                _databases[last] = null;
                _publications[last] = default;
                _leases[last] = default;
                return;
            }
        }
    }
}
