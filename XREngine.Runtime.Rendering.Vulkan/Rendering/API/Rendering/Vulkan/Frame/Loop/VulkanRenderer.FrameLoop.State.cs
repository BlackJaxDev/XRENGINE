namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Gets the output-owned state selected during bootstrap. Bootstrap still
    /// constructs the target driver in this transition cut; consumers use this
    /// authority rather than retaining their own target references.
    /// </summary>
    private VulkanOutputRuntime OutputRuntime => _outputRuntime;

    private VulkanFrameLoop FrameLoop => _frameLoop;

    // Compatibility constant for frame resources that are still renderer-owned.
    // The authoritative desktop loop uses the same value internally.
    private const int MAX_FRAMES_IN_FLIGHT = 2;

    private VulkanDesktopWsiTargetDriver DesktopWsiOutput
        => OutputRuntime.RequireDesktopWsiTarget();

    internal ulong VulkanFrameCounter
        => AcceptedDesktopFrameAttemptCount;

    /// <summary>
    /// Gets the number of desktop frame attempts that successfully entered the
    /// coordinator. Reentrant rejected callbacks are not counted.
    /// </summary>
    internal ulong AcceptedDesktopFrameAttemptCount
        => FrameLoop.AcceptedAttemptCount;

    /// <summary>
    /// Gets the most recently selected desktop in-flight slot.
    /// </summary>
    internal int CurrentDesktopFrameSlot
        => FrameLoop.CurrentFrameSlot;

    /// <summary>
    /// Gets whether at least one accepted desktop frame tick reached an
    /// observed completion/skip publication point.
    /// </summary>
    internal bool HasObservedDesktopFrameTick
        => FrameLoop.HasObservedTick;

    internal long LastDesktopFrameTickObservedTimestamp
        => FrameLoop.LastObservedTickTimestamp;

    /// <summary>
    /// Captures one coherent observation of the active desktop attempt.
    /// </summary>
    internal DesktopFrameActivitySnapshot CaptureDesktopFrameActivity()
        => FrameLoop.CaptureActivity();

    internal VulkanOutputRuntimeSnapshot CaptureOutputRuntimeSnapshot()
        => OutputRuntime.CaptureSnapshot();

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

    private void AdvanceDesktopFrameSlot(int completedFrameSlot)
        => FrameLoop.AdvanceFrameSlot(completedFrameSlot);

    private void RecordDesktopFrameTickObserved(long timestamp)
        => FrameLoop.RecordObservedTick(timestamp);
}
