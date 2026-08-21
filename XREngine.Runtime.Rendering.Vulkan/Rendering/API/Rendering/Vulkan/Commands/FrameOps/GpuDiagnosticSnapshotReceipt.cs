using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks whether every diagnostics-only snapshot copy for one destination was
/// recorded. A unique sequence also keeps command-buffer reuse from substituting
/// an older snapshot producer. Submission acceptance is checked separately by
/// the frame-loop path that owns the pending readback request.
/// </summary>
internal sealed class GpuDiagnosticSnapshotReceipt
{
    private static long s_nextSequence;
    private int _expectedCopyCount;
    private int _recordedCopyCount;

    internal GpuDiagnosticSnapshotReceipt(ulong frameId)
    {
        FrameId = frameId;
        Sequence = unchecked((ulong)Interlocked.Increment(ref s_nextSequence));
    }

    internal ulong FrameId { get; }
    internal ulong Sequence { get; }
    internal bool IsRecorded
    {
        get
        {
            int expected = Volatile.Read(ref _expectedCopyCount);
            return expected > 0 && Volatile.Read(ref _recordedCopyCount) >= expected;
        }
    }

    internal void RegisterCopy()
        => Interlocked.Increment(ref _expectedCopyCount);

    internal void MarkRecorded()
        => Interlocked.Increment(ref _recordedCopyCount);
}
