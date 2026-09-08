namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private VulkanAdvancedVisibilityResourceRuntime? _advancedVisibilityResources;
    internal object AdvancedVisibilityStorageGate { get; } = new();
    private bool[]? _advancedVisibilityQuarantinedSlots;
    private VulkanFrameDataArena? _advancedVisibilityStorageArena;
    private ulong _advancedVisibilityStorageGeneration;
    private readonly VulkanAdvancedVisibilityResourceRuntime?[] _advancedVisibilityOutputResources =
        new VulkanAdvancedVisibilityResourceRuntime?[VulkanAdvancedVisibilityOutputCapacity.Maximum];
    private readonly long[] _advancedVisibilityOutputBankIncarnations =
        new long[VulkanAdvancedVisibilityOutputCapacity.Maximum];
    private readonly long[] _advancedVisibilityOutputBackendGenerations =
        new long[VulkanAdvancedVisibilityOutputCapacity.Maximum];

    internal bool[] AdvancedVisibilityQuarantinedSlots
        => _advancedVisibilityQuarantinedSlots ??= new bool[Lifetime.Retirement.Framebuffers.Length];

    internal bool TryEnsureAdvancedVisibilityStorage(VulkanFrameDataArena arena, ulong capacity, uint alignment)
    {
        lock (AdvancedVisibilityStorageGate)
        {
            if (ReferenceEquals(_advancedVisibilityStorageArena, arena) &&
                _advancedVisibilityStorageGeneration == arena.Generation)
                return arena.HasReservedLaneCapacity(EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    _advancedVisibilityStorageGeneration, capacity);
            if (!arena.TryReserveLaneCapacity(EVulkanFrameDataLane.AdvancedVisibilityStorage, capacity, alignment))
                return false;
            _advancedVisibilityStorageArena = arena;
            _advancedVisibilityStorageGeneration = arena.Generation;
            return arena.HasReservedLaneCapacity(EVulkanFrameDataLane.AdvancedVisibilityStorage,
                _advancedVisibilityStorageGeneration, capacity);
        }
    }

    /// <summary>
    /// Lazily owns the fixed set-1 visibility producer lane. Creation does not
    /// allocate Vulkan objects; initialization remains at the device boundary.
    /// </summary>
    internal VulkanAdvancedVisibilityResourceRuntime AdvancedVisibilityResources
        => _advancedVisibilityResources ??= new VulkanAdvancedVisibilityResourceRuntime(
            this,
            Lifetime.Retirement.Framebuffers.Length);

    /// <summary>
    /// Initializes one output bank at reservation time. Its persistent occlusion,
    /// descriptors and frame-slot seals remain independent until device teardown.
    /// All banks transact against the arena under one shared storage gate.
    /// </summary>
    internal bool TryInitializeAdvancedVisibilityOutput(
        in AdvancedVisibilityFamilyReservation reservation,
        VulkanDeviceContext device,
        out string reason)
    {
        lock (AdvancedVisibilityStorageGate)
        {
            if (!reservation.IsValid || reservation.ReservationId > (ulong)_advancedVisibilityOutputResources.Length)
            {
                reason = "Advanced visibility output-bank capacity exceeded.";
                return false;
            }
            int index = checked((int)reservation.ReservationId - 1);
            // The first bank also supplies the program ABI layout. Every later
            // bank defines an identical layout and shares the same device lifetime.
            VulkanAdvancedVisibilityResourceRuntime bank = _advancedVisibilityOutputResources[index] ??=
                index == 0 ? AdvancedVisibilityResources : new(this, Lifetime.Retirement.Framebuffers.Length, reservation.ReservationId);
            if (!bank.TryInitialize(device, out reason))
                return false;
            if (!bank.TryAssignReservation(in reservation, out reason))
                return false;
            _advancedVisibilityOutputBankIncarnations[index] = reservation.BankIncarnation;
            _advancedVisibilityOutputBackendGenerations[index] = reservation.BackendGeneration;
            return true;
        }
    }

    internal VulkanAdvancedVisibilityResourceRuntime GetAdvancedVisibilityOutput(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        lock (AdvancedVisibilityStorageGate)
        {
            if (!reservation.IsValid || reservation.ReservationId > (ulong)_advancedVisibilityOutputResources.Length ||
                _advancedVisibilityOutputBankIncarnations[checked((int)reservation.ReservationId - 1)] != reservation.BankIncarnation ||
                _advancedVisibilityOutputBackendGenerations[checked((int)reservation.ReservationId - 1)] != reservation.BackendGeneration ||
                _advancedVisibilityOutputResources[checked((int)reservation.ReservationId - 1)] is not { IsReady: true } bank)
                throw new VulkanPlanPreconditionException("The exact Advanced output resource bank is unavailable.");
            return bank;
        }
    }

    internal void RetireAdvancedVisibilityOutputs()
    {
        lock (AdvancedVisibilityStorageGate)
        {
            for (int i = 1; i < _advancedVisibilityOutputResources.Length; i++)
                _advancedVisibilityOutputResources[i]?.RetireAll();
            _advancedVisibilityResources?.RetireAll();
            Array.Clear(_advancedVisibilityOutputResources);
        }
    }
}
