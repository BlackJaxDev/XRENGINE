using System.Threading;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns queued frame operations and the reusable capture workspace for each
/// recording thread. The renderer facade supplies policy and operation
/// construction, while this owner contains all mutable queue/capture state.
/// </summary>
internal sealed class VulkanFrameOperationQueue : IDisposable
{
    private const int AdvancedVisibilityLeaseCapacity = 16;

    private readonly ThreadLocal<ThreadWorkspace> _threadWorkspace =
        new(static () => new ThreadWorkspace(), trackAllValues: false);
    private readonly VulkanAdvancedVisibilityInputLease[]
        _advancedVisibilityInputLeases = CreateAdvancedVisibilityInputLeases();

    public Lock SyncRoot { get; } = new();
    public List<FrameOp> Pending { get; } = [];
    public FrameOp[] DrainedFrameOpsBuffer { get; set; } = [];
    public FrameOp[] DrainedTextureUploadFrameOpsBuffer { get; set; } = [];
    internal VulkanFrameOpDiagnosticsState Diagnostics { get; } = new();

    /// <summary>
    /// Gets the reusable workspace scoped to the calling recording thread.
    /// The first access on a thread allocates the workspace; warmed steady-state
    /// access and capture reuse are allocation-free.
    /// </summary>
    public ThreadWorkspace CurrentThread
        => _threadWorkspace.Value
            ?? throw new InvalidOperationException(
                "The Vulkan frame-operation queue has been disposed.");

    public void ReleaseCurrentThread()
        => CurrentThread.Reset();

    /// <summary>
    /// Retains one immutable visibility snapshot reference before the authored
    /// operation crosses into a deferred queue or capture cohort.
    /// </summary>
    internal bool TryAcquireAdvancedVisibilityInput(
        in VulkanAdvancedVisibilityStageRequest request,
        out VulkanAdvancedVisibilityInputLease lease,
        out string failureReason)
    {
        using (SyncRoot.EnterScope())
        {
            for (int index = 0;
                 index < _advancedVisibilityInputLeases.Length;
                 ++index)
            {
                VulkanAdvancedVisibilityInputLease candidate =
                    _advancedVisibilityInputLeases[index];
                if (candidate.MatchesRequest(in request) &&
                    candidate.TryRetain())
                {
                    lease = candidate;
                    failureReason = "Ready";
                    return true;
                }
            }

            for (int index = 0;
                 index < _advancedVisibilityInputLeases.Length;
                 ++index)
            {
                VulkanAdvancedVisibilityInputLease candidate =
                    _advancedVisibilityInputLeases[index];
                if (!candidate.IsAvailable ||
                    !candidate.TryCapture(in request, out failureReason))
                {
                    continue;
                }

                lease = candidate;
                return true;
            }
        }

        lease = null!;
        failureReason =
            $"The bounded advanced visibility authoring lease arena exhausted its " +
            $"{AdvancedVisibilityLeaseCapacity} concurrent families.";
        return false;
    }

    /// <summary>
    /// Publishes one fully prepared operation into the active capture or the shared
    /// pending stream. Producer-side validation and resource lowering must finish
    /// before this scheduling boundary.
    /// </summary>
    internal void EnqueuePrepared(FrameOp operation)
    {
        VulkanShadowAtlasDiagnostics.RecordEnqueuedOperation(operation);
        FrameOpCapture? capture = CurrentThread.Capture;
        if (capture is not null)
        {
            if (capture.ExcludeTextureUploads && operation is TextureUploadFrameOp)
            {
                using (SyncRoot.EnterScope())
                    Pending.Add(operation);
            }
            else
            {
                capture.Add(operation);
            }

            return;
        }

        using (SyncRoot.EnterScope())
            Pending.Add(operation);
    }

    internal bool TryBeginOrderedBatch()
    {
        if (CurrentThread.OrderedComputeBatchCapture is not null)
            return false;

        FrameOpCapture capture = CurrentThread.OrderedComputeBatchCaptureScratch ??= new FrameOpCapture();
        capture.Begin(CurrentThread.Capture, excludeTextureUploads: false);
        CurrentThread.OrderedComputeBatchCapture = capture;
        CurrentThread.Capture = capture;
        return true;
    }

    internal void CommitOrderedBatch()
    {
        FrameOpCapture capture = EndOrderedBatch();
        FrameOpCapture? previous = capture.Previous;
        if (previous is not null)
        {
            for (int index = 0; index < capture.Count; index++)
                previous.Add(capture.Buffer[index]);
            return;
        }

        using (SyncRoot.EnterScope())
            for (int index = 0; index < capture.Count; index++)
                Pending.Add(capture.Buffer[index]);
    }

    internal void RollbackOrderedBatch()
    {
        FrameOpCapture capture = EndOrderedBatch();
        for (int index = 0; index < capture.Count; index++)
        {
            capture.Buffer[index].ReleaseAuthoringSnapshot();
            if (capture.Buffer[index] is SubmissionMarkerOp marker)
                marker.Fence.Fail();
            if (capture.Buffer[index] is AdvancedVisibilityOp visibility)
                visibility.ReleaseInputLease();
        }
    }

    private FrameOpCapture EndOrderedBatch()
    {
        FrameOpCapture capture = CurrentThread.OrderedComputeBatchCapture
            ?? throw new InvalidOperationException("No ordered compute batch is active on this thread.");
        CurrentThread.Capture = capture.Previous;
        CurrentThread.OrderedComputeBatchCapture = null;
        return capture;
    }

    internal bool TryGetLastForTarget(XRFrameBuffer target, out FrameOp operation)
    {
        FrameOpCapture? capture = CurrentThread.Capture;
        if (capture is not null)
        {
            for (int index = capture.Count - 1; index >= 0; index--)
            {
                FrameOp candidate = capture.Buffer[index];
                if (Targets(candidate, target))
                {
                    operation = candidate;
                    return true;
                }
            }
        }

        using (SyncRoot.EnterScope())
        {
            for (int index = Pending.Count - 1; index >= 0; index--)
            {
                FrameOp candidate = Pending[index];
                if (Targets(candidate, target))
                {
                    operation = candidate;
                    return true;
                }
            }
        }

        operation = null!;
        return false;
    }

    private static bool Targets(FrameOp operation, XRFrameBuffer target)
        => operation is not PublishFramebufferForSamplingOp &&
           ReferenceEquals(operation.Target, target);

    internal bool EnqueuePreparedQuery(
        VkRenderQuery query,
        in RenderQueryDescriptor descriptor,
        ERenderQueryOperation operation,
        int passIndex,
        XRFrameBuffer? target,
        in FrameOpContext context,
        Silk.NET.Vulkan.PipelineStageFlags2 timestampStage = Silk.NET.Vulkan.PipelineStageFlags2.AllCommandsBit,
        uint pointIndex = 0u)
    {
        if (descriptor.Kind == ERenderQueryKind.Occlusion &&
            RenderDiagnosticsFlags.VkSkipOcclusionQueryOps &&
            (operation == ERenderQueryOperation.Begin || CurrentThread.RenderQueryBracketDepth == 0))
        {
            Debug.VulkanWarningEvery(
                "Vulkan.OcclusionQueryOpsSkipped",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Skipping occlusion QueryOp {0} for command-chain ceiling diagnostics ({1}=1). Query results remain stale/conservative.",
                operation,
                XREngineEnvironmentVariables.VkSkipOcclusionQueryOps);
            return false;
        }

        EnqueuePrepared(new QueryOp(
            passIndex,
            target,
            query,
            descriptor,
            operation,
            context,
            timestampStage,
            pointIndex));
        if (operation == ERenderQueryOperation.Begin)
            CurrentThread.RenderQueryBracketDepth++;
        else if (operation == ERenderQueryOperation.End && CurrentThread.RenderQueryBracketDepth > 0)
            CurrentThread.RenderQueryBracketDepth--;
        return true;
    }

    internal FrameOp[] Capture(Action emitOperations, bool excludeTextureUploads)
    {
        FrameOpCapture? previous = CurrentThread.Capture;
        FrameOpCapture capture = RentCapture(previous, excludeTextureUploads);
        CurrentThread.Capture = capture;
        try
        {
            emitOperations();
        }
        catch
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                capture.Buffer.AsSpan(0, capture.Count));
            throw;
        }
        finally
        {
            CurrentThread.Capture = previous;
        }

        return CopyCapture(capture);
    }

    internal FrameOp[] Capture(
        IOpenXrEyeFrameOpEmitter emitter,
        in OpenXrEyeFrameOpEmission emission,
        bool excludeTextureUploads)
    {
        FrameOpCapture? previous = CurrentThread.Capture;
        FrameOpCapture capture = RentCapture(previous, excludeTextureUploads);
        CurrentThread.Capture = capture;
        try
        {
            emitter.Emit(emission);
        }
        catch
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                capture.Buffer.AsSpan(0, capture.Count));
            throw;
        }
        finally
        {
            CurrentThread.Capture = previous;
        }

        return CopyCapture(capture);
    }

    private FrameOpCapture RentCapture(FrameOpCapture? previous, bool excludeTextureUploads)
    {
        FrameOpCapture capture = previous is null
            ? CurrentThread.CaptureScratch ??= new FrameOpCapture()
            : new FrameOpCapture();
        capture.Begin(previous, excludeTextureUploads);
        return capture;
    }

    private FrameOp[] CopyCapture(FrameOpCapture capture)
    {
        int operationCount = capture.Count;
        if (operationCount == 0)
            return [];

        Dictionary<int, FrameOp[]> buffers = CurrentThread.CaptureBuffersByCount;
        if (!buffers.TryGetValue(operationCount, out FrameOp[]? result))
        {
            result = new FrameOp[operationCount];
            buffers.Add(operationCount, result);
        }
        else
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(result);
        }

        Array.Copy(capture.Buffer, result, operationCount);
        return result;
    }

    internal FrameOp[] DrainPending()
    {
        using (SyncRoot.EnterScope())
        {
            if (Pending.Count == 0)
                return [];

            int operationCount = Pending.Count;
            if (DrainedFrameOpsBuffer.Length != operationCount)
                DrainedFrameOpsBuffer = new FrameOp[operationCount];

            Pending.CopyTo(DrainedFrameOpsBuffer);
            Pending.Clear();
            return DrainedFrameOpsBuffer;
        }
    }

    /// <summary>Atomically drains scene and upload operations for one frame-plan preparation.</summary>
    internal FrameOp[] DrainForPrimary(out FrameOp[] textureUploadOperations)
    {
        using (SyncRoot.EnterScope())
        {
            int operationCount = Pending.Count;
            if (operationCount == 0)
            {
                textureUploadOperations = [];
                return [];
            }

            int uploadCount = 0;
            for (int index = 0; index < operationCount; index++)
                if (Pending[index] is TextureUploadFrameOp)
                    uploadCount++;

            int sceneCount = operationCount - uploadCount;
            if (DrainedFrameOpsBuffer.Length != sceneCount)
                DrainedFrameOpsBuffer = new FrameOp[sceneCount];
            if (DrainedTextureUploadFrameOpsBuffer.Length != uploadCount)
                DrainedTextureUploadFrameOpsBuffer = new FrameOp[uploadCount];

            int sceneIndex = 0;
            int uploadIndex = 0;
            for (int index = 0; index < operationCount; index++)
            {
                FrameOp operation = Pending[index];
                if (operation is TextureUploadFrameOp)
                    DrainedTextureUploadFrameOpsBuffer[uploadIndex++] = operation;
                else
                    DrainedFrameOpsBuffer[sceneIndex++] = operation;
            }

            Pending.Clear();
            textureUploadOperations = DrainedTextureUploadFrameOpsBuffer;
            return DrainedFrameOpsBuffer;
        }
    }

    /// <summary>Drains only uploads while retaining scene operations in queue order.</summary>
    internal FrameOp[] DrainTextureUploads()
    {
        using (SyncRoot.EnterScope())
        {
            int operationCount = Pending.Count;
            int uploadCount = 0;
            for (int index = 0; index < operationCount; index++)
                if (Pending[index] is TextureUploadFrameOp)
                    uploadCount++;

            if (uploadCount == 0)
                return [];
            if (DrainedTextureUploadFrameOpsBuffer.Length != uploadCount)
                DrainedTextureUploadFrameOpsBuffer = new FrameOp[uploadCount];

            int retainedIndex = 0;
            int uploadIndex = 0;
            for (int index = 0; index < operationCount; index++)
            {
                FrameOp operation = Pending[index];
                if (operation is TextureUploadFrameOp)
                    DrainedTextureUploadFrameOpsBuffer[uploadIndex++] = operation;
                else
                    Pending[retainedIndex++] = operation;
            }

            if (retainedIndex < Pending.Count)
                Pending.RemoveRange(retainedIndex, Pending.Count - retainedIndex);
            return DrainedTextureUploadFrameOpsBuffer;
        }
    }

    /// <summary>Drains scene operations while retaining uploads in queue order.</summary>
    internal FrameOp[] DrainExcludingTextureUploads()
    {
        using (SyncRoot.EnterScope())
        {
            int operationCount = Pending.Count;
            int uploadCount = 0;
            for (int index = 0; index < operationCount; index++)
                if (Pending[index] is TextureUploadFrameOp)
                    uploadCount++;

            int drainedCount = operationCount - uploadCount;
            if (drainedCount == 0)
                return [];
            if (DrainedFrameOpsBuffer.Length != drainedCount)
                DrainedFrameOpsBuffer = new FrameOp[drainedCount];

            int drainedIndex = 0;
            int retainedIndex = 0;
            for (int index = 0; index < operationCount; index++)
            {
                FrameOp operation = Pending[index];
                if (operation is TextureUploadFrameOp)
                    Pending[retainedIndex++] = operation;
                else
                    DrainedFrameOpsBuffer[drainedIndex++] = operation;
            }

            if (retainedIndex < Pending.Count)
                Pending.RemoveRange(retainedIndex, Pending.Count - retainedIndex);
            return DrainedFrameOpsBuffer;
        }
    }

    public void Dispose()
    {
        using (SyncRoot.EnterScope())
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                CollectionsMarshal.AsSpan(Pending));
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                DrainedFrameOpsBuffer);
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                DrainedTextureUploadFrameOpsBuffer);
            Pending.Clear();
        }

        _threadWorkspace.Dispose();
    }

    private static VulkanAdvancedVisibilityInputLease[]
        CreateAdvancedVisibilityInputLeases()
    {
        VulkanAdvancedVisibilityInputLease[] leases =
            new VulkanAdvancedVisibilityInputLease[
                AdvancedVisibilityLeaseCapacity];
        for (int index = 0; index < leases.Length; ++index)
            leases[index] = new();
        return leases;
    }

    internal sealed class ThreadWorkspace
    {
        public FrameOpCapture? Capture;
        public FrameOpCapture? CaptureScratch;
        public FrameOpCapture? OrderedComputeBatchCapture;
        public FrameOpCapture? OrderedComputeBatchCaptureScratch;
        public Dictionary<int, FrameOp[]> CaptureBuffersByCount { get; } = [];
        public int RenderQueryBracketDepth;

        public void Reset()
        {
            Capture = null;
            CaptureScratch = null;
            OrderedComputeBatchCapture = null;
            OrderedComputeBatchCaptureScratch = null;
            CaptureBuffersByCount.Clear();
            RenderQueryBracketDepth = 0;
        }
    }
}
