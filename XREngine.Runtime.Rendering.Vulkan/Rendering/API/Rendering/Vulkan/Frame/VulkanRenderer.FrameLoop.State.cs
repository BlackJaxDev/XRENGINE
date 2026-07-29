using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private VulkanDesktopFrameCoordinator? _desktopFrameCoordinator;

    private VulkanDesktopFrameCoordinator DesktopFrameCoordinator
        => _desktopFrameCoordinator ??= new VulkanDesktopFrameCoordinator(this);

    private const int MAX_FRAMES_IN_FLIGHT = 2;

    private readonly DesktopFrameActivityState _desktopFrameActivity = new();
    private readonly object _desktopFrameRetirementGate = new();
    private int _desktopFrameSlot;
    private ulong _vkDebugFrameCounter;
    private long _lastDesktopFrameTickObservedTimestamp;

    internal ulong VulkanFrameCounter
        => AcceptedDesktopFrameAttemptCount;

    /// <summary>
    /// Gets the number of desktop frame attempts that successfully entered the
    /// renderer. Reentrant rejected callbacks are not counted.
    /// </summary>
    internal ulong AcceptedDesktopFrameAttemptCount
        => Volatile.Read(ref _vkDebugFrameCounter);

    /// <summary>
    /// Gets the most recently selected desktop in-flight slot.
    /// </summary>
    internal int CurrentDesktopFrameSlot
        => Volatile.Read(ref _desktopFrameSlot);

    /// <summary>
    /// Gets whether at least one accepted desktop frame tick reached an
    /// observed completion/skip publication point.
    /// </summary>
    internal bool HasObservedDesktopFrameTick
        => Volatile.Read(
            ref _lastDesktopFrameTickObservedTimestamp) != 0;

    /// <summary>
    /// Captures one coherent observation of the active desktop attempt.
    /// </summary>
    internal DesktopFrameActivitySnapshot CaptureDesktopFrameActivity()
        => _desktopFrameActivity.Capture();

    /// <summary>
    /// Captures the active immutable attempt identity when present, or a named
    /// thread-safe fallback containing the last accepted attempt and next
    /// desktop slot for diagnostics emitted outside a desktop attempt.
    /// </summary>
    internal DesktopFrameActivitySnapshot CaptureDesktopFrameDiagnosticState()
    {
        DesktopFrameActivitySnapshot activity = CaptureDesktopFrameActivity();
        return activity.IsActive
            ? activity
            : new DesktopFrameActivitySnapshot(
                false,
                AcceptedDesktopFrameAttemptCount,
                CurrentDesktopFrameSlot);
    }

    /// <summary>
    /// Attempts to enter the desktop frame lifecycle and captures its immutable
    /// identity without advancing the accepted-attempt counter on rejection.
    /// </summary>
    private bool TryEnterDesktopFrameAttempt(out DesktopFrameIdentity identity)
    {
        lock (_desktopFrameRetirementGate)
        {
            int frameSlot = CurrentDesktopFrameSlot;
            ulong frameNumber =
                checked(AcceptedDesktopFrameAttemptCount + 1UL);
            if (!_desktopFrameActivity.TryEnter(
                    frameNumber,
                    frameSlot,
                    out long activityPublicationToken))
            {
                identity = default;
                return false;
            }

            Volatile.Write(ref _vkDebugFrameCounter, frameNumber);
            identity = new DesktopFrameIdentity(
                frameNumber,
                frameSlot,
                Stopwatch.GetTimestamp(),
                activityPublicationToken);
            return true;
        }
    }

    /// <summary>
    /// Releases the active publication owned by
    /// <paramref name="identity"/>. A stale identity cannot clear a newer
    /// attempt.
    /// </summary>
    private void ExitDesktopFrameAttempt(in DesktopFrameIdentity identity)
    {
        lock (_desktopFrameRetirementGate)
            _desktopFrameActivity.TryExit(identity.ActivityPublicationToken);
    }

    /// <summary>
    /// Publishes that an accepted desktop frame tick reached its established
    /// completion/skip observation point.
    /// </summary>
    private void RecordDesktopFrameTickObserved(long timestamp)
        => Volatile.Write(
            ref _lastDesktopFrameTickObservedTimestamp,
            timestamp);
}
