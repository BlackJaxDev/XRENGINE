namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Point-in-time state of the diagnostic staging ring (<see cref="VulkanGpuDiagnosticReadbackSidecar"/>).
/// A ring whose slots stay <c>Reserved</c> for many frames has a reservation that no submission
/// settled, so new copies are dropped and the counter receipts stop.
/// </summary>
/// <param name="Capacity">Number of ring slots.</param>
/// <param name="Idle">Slots free for a new reservation.</param>
/// <param name="Reserved">Slots reserved but not yet submitted.</param>
/// <param name="ReservedPrimaryAttached">Reserved slots whose copy is attached to a primary command buffer.</param>
/// <param name="Submitted">Slots submitted and waiting for their completion signal.</param>
/// <param name="Decoding">Slots whose staging data is being read.</param>
/// <param name="OldestReservedFrameIdentity">Smallest frame identity of a reserved slot, or 0.</param>
/// <param name="DroppedReservations">Cumulative reservations dropped because no slot was free.</param>
/// <param name="CompletedReservations">Cumulative reservations that completed and were decoded.</param>
internal readonly record struct VulkanGpuDiagnosticReadbackSidecarDiagnostic(
    int Capacity,
    int Idle,
    int Reserved,
    int ReservedPrimaryAttached,
    int Submitted,
    int Decoding,
    ulong OldestReservedFrameIdentity,
    long DroppedReservations,
    long CompletedReservations);
