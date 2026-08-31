using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Latest desktop frame-slot timeline timeout. This is intentionally updated
/// only after a timed Vulkan wait expires, so normal frame submission has no
/// diagnostic allocation or counter query.
/// </summary>
internal sealed class VulkanFrameWaitDiagnostics(
    ulong semaphoreHandle,
    ulong targetValue,
    ulong currentValue,
    Result counterQueryResult,
    ulong currentFrameNumber,
    int currentFrameSlot,
    int nextFrameSlot,
    ulong currentSlotAcceptedSignal,
    ulong nextSlotAcceptedSignal,
    ulong latestReservedGraphicsSignal)
{
    private static VulkanFrameWaitDiagnostics? _lastTimeoutSnapshot;

    /// <summary>Reflection-readable latest timeout snapshot, or null before the first timeout.</summary>
    public static VulkanFrameWaitDiagnostics? LastTimeoutSnapshot
        => Volatile.Read(ref _lastTimeoutSnapshot);

    public ulong SemaphoreHandle { get; } = semaphoreHandle;
    public ulong TargetValue { get; } = targetValue;
    public ulong CurrentValue { get; } = currentValue;
    public Result CounterQueryResult { get; } = counterQueryResult;
    public ulong CurrentFrameNumber { get; } = currentFrameNumber;
    public int CurrentFrameSlot { get; } = currentFrameSlot;
    public int NextFrameSlot { get; } = nextFrameSlot;
    /// <summary>Accepted graphics signal currently assigned to the active CPU slot.</summary>
    public ulong CurrentSlotAcceptedSignal { get; } = currentSlotAcceptedSignal;
    /// <summary>Accepted graphics signal that the next CPU slot is waiting to reuse.</summary>
    public ulong NextSlotAcceptedSignal { get; } = nextSlotAcceptedSignal;
    /// <summary>Latest reserved graphics timeline value; this is not an acceptance receipt.</summary>
    public ulong LatestReservedGraphicsSignal { get; } = latestReservedGraphicsSignal;

    internal static void Capture(
        ulong semaphoreHandle,
        ulong targetValue,
        ulong currentValue,
        Result counterQueryResult,
        ulong currentFrameNumber,
        int currentFrameSlot,
        int nextFrameSlot,
        ulong currentSlotAcceptedSignal,
        ulong nextSlotAcceptedSignal,
        ulong latestReservedGraphicsSignal)
        => Volatile.Write(
            ref _lastTimeoutSnapshot,
            new VulkanFrameWaitDiagnostics(
                semaphoreHandle,
                targetValue,
                currentValue,
                counterQueryResult,
                currentFrameNumber,
                currentFrameSlot,
                nextFrameSlot,
                currentSlotAcceptedSignal,
                nextSlotAcceptedSignal,
                latestReservedGraphicsSignal));
}
