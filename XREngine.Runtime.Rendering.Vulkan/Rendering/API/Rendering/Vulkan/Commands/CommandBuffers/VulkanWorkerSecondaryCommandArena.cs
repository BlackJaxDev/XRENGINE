using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns one worker's secondary-command pools and reusable artifact slots for
/// every indexed frame slot.
/// </summary>
internal sealed class VulkanWorkerSecondaryCommandArena(int workerIndex)
{
    private CommandPool[] _poolsByFrameSlot = [];
    private HashSet<VulkanRecordedCommandArtifact>[] _artifactsByFrameSlot = [];
    private int _recordingThreadId;

    internal int WorkerIndex { get; } = workerIndex;
    internal ulong Identity => unchecked((ulong)(WorkerIndex + 1));
    internal int FrameSlotCount => _poolsByFrameSlot.Length;
    internal ulong Generation { get; private set; }
    internal bool IsRecording => Volatile.Read(ref _recordingThreadId) != 0;

    internal static RecordingLease EnterRecording(
        VulkanWorkerSecondaryCommandArena? arena)
        => new(arena);

    internal void Initialize(CommandPool[] poolsByFrameSlot)
    {
        ArgumentNullException.ThrowIfNull(poolsByFrameSlot);
        if (_poolsByFrameSlot.Length != 0)
            throw new InvalidOperationException("A Vulkan worker secondary arena cannot be initialized twice.");

        _poolsByFrameSlot = poolsByFrameSlot;
        _artifactsByFrameSlot =
            new HashSet<VulkanRecordedCommandArtifact>[poolsByFrameSlot.Length];
        for (int frameSlot = 0; frameSlot < poolsByFrameSlot.Length; frameSlot++)
        {
            _artifactsByFrameSlot[frameSlot] =
                new HashSet<VulkanRecordedCommandArtifact>(64);
        }

        AdvanceGeneration();
    }

    internal CommandPool GetPool(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)_poolsByFrameSlot.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        return _poolsByFrameSlot[frameSlot];
    }

    internal int GetArtifactCount(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)_artifactsByFrameSlot.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        return _artifactsByFrameSlot[frameSlot].Count;
    }

    /// <summary>
    /// Returns whether resetting the whole frame-slot pool would preserve every
    /// reusable recording and every primary-to-secondary lifetime pin.
    /// </summary>
    internal bool CanResetPoolWithoutDiscardingReusableArtifacts(
        int frameSlot,
        out int reusableArtifactCount,
        out int pendingArtifactCount)
    {
        if ((uint)frameSlot >= (uint)_artifactsByFrameSlot.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));

        reusableArtifactCount = 0;
        pendingArtifactCount = 0;
        foreach (VulkanRecordedCommandArtifact artifact in
                 _artifactsByFrameSlot[frameSlot])
        {
            if (artifact.IsExecutable)
                reusableArtifactCount++;
            if (artifact.IsPending)
                pendingArtifactCount++;
        }

        return !IsRecording &&
            reusableArtifactCount == 0 &&
            pendingArtifactCount == 0;
    }

    internal void Attach(VulkanRecordedCommandArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if ((uint)artifact.FrameSlot >= (uint)_artifactsByFrameSlot.Length)
        {
            throw new InvalidOperationException(
                $"Artifact frame slot {artifact.FrameSlot} is outside worker arena {WorkerIndex}.");
        }

        if (_artifactsByFrameSlot[artifact.FrameSlot].Add(artifact))
            AdvanceGeneration();
    }

    internal void Detach(VulkanRecordedCommandArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if ((uint)artifact.FrameSlot >= (uint)_artifactsByFrameSlot.Length)
            return;

        if (_artifactsByFrameSlot[artifact.FrameSlot].Remove(artifact))
            AdvanceGeneration();
    }

    internal void ClearAfterPoolRetirement()
    {
        if (IsRecording)
        {
            throw new InvalidOperationException(
                $"Worker arena {WorkerIndex} cannot retire pools while recording.");
        }

        for (int frameSlot = 0; frameSlot < _artifactsByFrameSlot.Length; frameSlot++)
        {
            if (_artifactsByFrameSlot[frameSlot].Count != 0)
            {
                throw new InvalidOperationException(
                    $"Worker arena {WorkerIndex} frame slot {frameSlot} still owns " +
                    $"{_artifactsByFrameSlot[frameSlot].Count} recorded artifact(s) during pool retirement.");
            }
        }

        _poolsByFrameSlot = [];
        _artifactsByFrameSlot = [];
        AdvanceGeneration();
    }

    private void AdvanceGeneration()
        => Generation = VulkanGeneration.NextNonZero(Generation);

    private void AcquireRecording()
    {
        int threadId = Environment.CurrentManagedThreadId;
        int owner = Interlocked.CompareExchange(
            ref _recordingThreadId,
            threadId,
            comparand: 0);
        if (owner != 0)
        {
            throw new InvalidOperationException(
                $"Worker arena {WorkerIndex} is already owned for recording by thread {owner}; " +
                $"thread {threadId} cannot concurrently access its command pools.");
        }
    }

    private void ReleaseRecording()
    {
        int threadId = Environment.CurrentManagedThreadId;
        int owner = Interlocked.CompareExchange(
            ref _recordingThreadId,
            value: 0,
            comparand: threadId);
        if (owner != threadId)
        {
            throw new InvalidOperationException(
                $"Worker arena {WorkerIndex} recording ownership belongs to thread {owner}, " +
                $"not releasing thread {threadId}.");
        }
    }

    internal readonly ref struct RecordingLease
    {
        private readonly VulkanWorkerSecondaryCommandArena? _arena;

        internal RecordingLease(VulkanWorkerSecondaryCommandArena? arena)
        {
            _arena = arena;
            _arena?.AcquireRecording();
        }

        public void Dispose()
            => _arena?.ReleaseRecording();
    }
}
