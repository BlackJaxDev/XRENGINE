namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private VulkanDesktopFrameCoordinator? _desktopFrameCoordinator;

    private VulkanDesktopFrameCoordinator DesktopFrameCoordinator
        => _desktopFrameCoordinator ??= new VulkanDesktopFrameCoordinator(this);

    private const int MAX_FRAMES_IN_FLIGHT = 2;

    internal ulong VulkanFrameCounter
        => AcceptedDesktopFrameAttemptCount;

    /// <summary>
    /// Gets the number of desktop frame attempts that successfully entered the
    /// coordinator. Reentrant rejected callbacks are not counted.
    /// </summary>
    internal ulong AcceptedDesktopFrameAttemptCount
        => DesktopFrameCoordinator.AcceptedAttemptCount;

    /// <summary>
    /// Gets the most recently selected desktop in-flight slot.
    /// </summary>
    internal int CurrentDesktopFrameSlot
        => DesktopFrameCoordinator.CurrentFrameSlot;

    /// <summary>
    /// Gets whether at least one accepted desktop frame tick reached an
    /// observed completion/skip publication point.
    /// </summary>
    internal bool HasObservedDesktopFrameTick
        => DesktopFrameCoordinator.HasObservedTick;

    internal long LastDesktopFrameTickObservedTimestamp
        => DesktopFrameCoordinator.LastObservedTickTimestamp;

    /// <summary>
    /// Captures one coherent observation of the active desktop attempt.
    /// </summary>
    internal DesktopFrameActivitySnapshot CaptureDesktopFrameActivity()
        => DesktopFrameCoordinator.CaptureActivity();

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

    private bool TryEnterDesktopFrameAttempt(out DesktopFrameIdentity identity)
        => DesktopFrameCoordinator.TryEnter(out identity);

    private void ExitDesktopFrameAttempt(in DesktopFrameIdentity identity)
        => DesktopFrameCoordinator.Exit(in identity);

    private void AdvanceDesktopFrameSlot(int completedFrameSlot)
        => DesktopFrameCoordinator.AdvanceFrameSlot(
            completedFrameSlot,
            MAX_FRAMES_IN_FLIGHT);

    private void RecordDesktopFrameTickObserved(long timestamp)
        => DesktopFrameCoordinator.RecordObservedTick(timestamp);
}