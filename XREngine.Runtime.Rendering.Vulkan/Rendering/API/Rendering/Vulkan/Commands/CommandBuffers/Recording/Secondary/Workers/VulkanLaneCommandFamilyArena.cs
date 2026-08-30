using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns one logical render lane's transient and retained command pools for one
/// frame slot and queue family. Only the lane which owns this arena may record
/// from either pool.
/// </summary>
internal sealed class VulkanLaneCommandFamilyArena
{
    private readonly HashSet<VulkanRecordedCommandArtifact> _retainedArtifacts = new(64);
    private int _recordingThreadId;
    private int _recordingDepth;
    private bool _transientPoolUsed;
    private ulong _lastTransientPreparationIdentity;

    internal VulkanLaneCommandFamilyArena(
        int laneId,
        int frameSlot,
        uint queueFamilyIndex,
        CommandPool transientPool,
        CommandPool retainedPool)
    {
        if (laneId < 0)
            throw new ArgumentOutOfRangeException(nameof(laneId));
        if (frameSlot < 0)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        if (transientPool.Handle == 0)
            throw new ArgumentException("A lane command arena requires a transient pool.", nameof(transientPool));
        if (retainedPool.Handle == 0)
            throw new ArgumentException("A lane command arena requires a retained pool.", nameof(retainedPool));
        if (transientPool.Handle == retainedPool.Handle)
            throw new ArgumentException("Transient and retained Vulkan command pools must be distinct.", nameof(retainedPool));

        LaneId = laneId;
        FrameSlot = frameSlot;
        QueueFamilyIndex = queueFamilyIndex;
        TransientPool = transientPool;
        RetainedPool = retainedPool;
    }

    internal int LaneId { get; }
    internal int FrameSlot { get; }
    internal uint QueueFamilyIndex { get; }
    internal CommandPool TransientPool { get; private set; }
    internal CommandPool RetainedPool { get; private set; }
    internal ulong Identity => RetainedPool.Handle;
    internal int RetainedArtifactCount => _retainedArtifacts.Count;
    internal bool IsRecording => Volatile.Read(ref _recordingThreadId) != 0;

    internal static RecordingLease EnterRecording(VulkanLaneCommandFamilyArena? arena)
        => new(arena);

    internal void Attach(VulkanRecordedCommandArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.OwnerPool.Handle != RetainedPool.Handle)
        {
            throw new InvalidOperationException(
                $"Artifact pool 0x{artifact.OwnerPool.Handle:X} is not retained arena pool " +
                $"0x{RetainedPool.Handle:X} for lane {LaneId}, slot {FrameSlot}, family {QueueFamilyIndex}.");
        }

        _retainedArtifacts.Add(artifact);
    }

    internal void Detach(VulkanRecordedCommandArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        _retainedArtifacts.Remove(artifact);
    }

    internal void MarkTransientPoolUsed()
        => _transientPoolUsed = true;

    /// <summary>
    /// Resets the transient pool once for a completed frame identity. A pool
    /// which has emitted work cannot be reset without exact completion proof.
    /// </summary>
    internal void PrepareTransientPool(
        VulkanCommandRuntime runtime,
        ulong preparationIdentity,
        bool priorUseCompletionProven)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (preparationIdentity == 0)
            throw new ArgumentOutOfRangeException(nameof(preparationIdentity));
        if (_lastTransientPreparationIdentity == preparationIdentity)
            return;
        if (IsRecording)
        {
            throw new InvalidOperationException(
                $"Cannot reset transient Vulkan command pool for lane {LaneId}, slot {FrameSlot}, " +
                $"family {QueueFamilyIndex} while it is recording.");
        }
        if (_transientPoolUsed && !priorUseCompletionProven)
        {
            throw new InvalidOperationException(
                $"Transient Vulkan command pool reuse for lane {LaneId}, slot {FrameSlot}, family " +
                $"{QueueFamilyIndex} requires exact completion of its prior use.");
        }

        Result result = runtime.ResetVulkanCommandPoolTracked(
            TransientPool,
            $"RenderLane[{LaneId}].FrameSlot[{FrameSlot}].QueueFamily[{QueueFamilyIndex}].Transient");
        if (result != Result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to reset transient Vulkan command pool for lane {LaneId}, slot {FrameSlot}, " +
                $"family {QueueFamilyIndex}: {result}.");
        }

        _transientPoolUsed = false;
        _lastTransientPreparationIdentity = preparationIdentity;
    }

    internal void ClearAfterRetirement()
    {
        if (IsRecording)
        {
            throw new InvalidOperationException(
                $"Lane {LaneId}, slot {FrameSlot}, family {QueueFamilyIndex} cannot retire while recording.");
        }
        if (_retainedArtifacts.Count != 0)
        {
            throw new InvalidOperationException(
                $"Lane {LaneId}, slot {FrameSlot}, family {QueueFamilyIndex} still owns " +
                $"{_retainedArtifacts.Count} retained command artifact(s) during pool retirement.");
        }

        TransientPool = default;
        RetainedPool = default;
        _transientPoolUsed = false;
        _lastTransientPreparationIdentity = 0;
    }

    private void AcquireRecording()
    {
        int threadId = Environment.CurrentManagedThreadId;
        int owner = Volatile.Read(ref _recordingThreadId);
        if (owner == threadId)
        {
            _recordingDepth++;
            return;
        }

        owner = Interlocked.CompareExchange(ref _recordingThreadId, threadId, comparand: 0);
        if (owner != 0)
        {
            throw new InvalidOperationException(
                $"Lane {LaneId}, slot {FrameSlot}, family {QueueFamilyIndex} is already owned " +
                $"for recording by thread {owner}; thread {threadId} cannot access its command pools.");
        }

        _recordingDepth = 1;
    }

    private void ReleaseRecording()
    {
        int threadId = Environment.CurrentManagedThreadId;
        int owner = Volatile.Read(ref _recordingThreadId);
        if (owner != threadId)
        {
            throw new InvalidOperationException(
                $"Lane {LaneId}, slot {FrameSlot}, family {QueueFamilyIndex} recording ownership " +
                $"belongs to thread {owner}, not releasing thread {threadId}.");
        }

        if (--_recordingDepth != 0)
            return;

        Volatile.Write(ref _recordingThreadId, 0);
    }

    internal readonly ref struct RecordingLease
    {
        private readonly VulkanLaneCommandFamilyArena? _arena;

        internal RecordingLease(VulkanLaneCommandFamilyArena? arena)
        {
            _arena = arena;
            _arena?.AcquireRecording();
        }

        public void Dispose()
            => _arena?.ReleaseRecording();
    }
}
