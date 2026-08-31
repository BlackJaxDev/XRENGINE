using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private readonly ulong _explicitProductionReceiptOwnerIdentity = unchecked((ulong)Random.Shared.NextInt64(1, long.MaxValue));
    private VulkanExplicitProductionSubmissionReceipt _lastExplicitProductionReceipt;
    private VulkanExplicitProductionSubmissionReceipt _currentExplicitProductionReadbackReceipt;
    private VulkanSealedResourceDependency[] _currentExplicitProductionResources = [];
    private int _currentExplicitProductionResourceCount;

    /// <summary>
    /// Invalidates the mutable target's last-output read authority before every
    /// explicit attempt. Completion queries retain the last accepted production
    /// receipt so callers can still observe prior timeline completion.
    /// </summary>
    internal void InvalidateExplicitProductionReadbackAuthority()
    {
        lock (_resourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            _currentExplicitProductionReadbackReceipt = default;
            Array.Clear(_currentExplicitProductionResources, 0, _currentExplicitProductionResourceCount);
            _currentExplicitProductionResourceCount = 0;
        }
    }

    private void CaptureExplicitProductionReadbackResources(SealedSubmissionContract? contract)
    {
        lock (_resourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            // A color readback resets/reseals the same primary command buffer.
            // Keep independent values for the production receipt until the next
            // attempt invalidates it; never retain mutable sealing workspace.
            ReadOnlySpan<VulkanSealedResourceDependency> resources = contract?.Resources ?? [];
            if (_currentExplicitProductionResources.Length < resources.Length)
                Array.Resize(ref _currentExplicitProductionResources, Math.Max(resources.Length, 64));
            resources.CopyTo(_currentExplicitProductionResources);
            _currentExplicitProductionResourceCount = resources.Length;
        }
    }

    private SealedSubmissionContract? CaptureExplicitRecordedContract(CommandBuffer commandBuffer)
    {
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (!tracker.CommandBufferLifetimes.TryGetValue(unchecked((ulong)(nuint)commandBuffer.Handle), out var command) ||
                command.SealedSubmissionContract is not { } contract ||
                contract.LifetimeRecordingGeneration != command.RecordingGeneration ||
                contract.StableCommandIdentity != command.StableCommandIdentity)
                return null;
            return contract;
        }
    }

    private static bool TryFindRecordedBufferGeneration(SealedSubmissionContract? contract,
        VulkanResourceLifetimeKey key, out ulong generation)
        => TryFindRecordedBufferGeneration(contract?.Resources ?? [], key, out generation);

    private static bool TryFindRecordedBufferGeneration(ReadOnlySpan<VulkanSealedResourceDependency> resources,
        VulkanResourceLifetimeKey key, out ulong generation)
    {
        foreach (VulkanSealedResourceDependency dependency in resources)
            if (dependency.Key == key && dependency.Slot.Generation == dependency.Generation)
            {
                generation = dependency.Generation;
                return true;
            }
        generation = 0;
        return false;
    }

    internal bool TryGetExplicitProductionSubmissionCompletion(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        out bool completed)
    {
        completed = false;
        if (!IsAuthenticExplicitProductionReceipt(in receipt))
            return false;

        completed = HasTimelineValueCompleted(
            _commandRuntime.Synchronization._graphicsTimelineSemaphore,
            receipt.GraphicsTimelineSignal);
        return true;
    }

    internal bool TryReadbackExplicitProductionColor(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        int maxByteCount,
        ImageLayout sourceLayout,
        out byte[]? color)
    {
        color = null;
        if (!IsCurrentExplicitProductionReceipt(in receipt) ||
            !TryGetExplicitProductionSubmissionCompletion(in receipt, out bool completed) || !completed)
            return false;

        color = RequireExplicitFrameTarget().ReadbackLastSubmittedColor(maxByteCount, sourceLayout);
        return true;
    }

    internal bool TryReadbackExplicitProductionBuffer(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        XRDataBuffer sourceBuffer,
        uint sourceByteOffset,
        Span<byte> destination,
        out string route)
    {
        route = "<receipt-not-complete>";
        if (!IsCurrentExplicitProductionReceipt(in receipt) ||
            !TryGetExplicitProductionSubmissionCompletion(in receipt, out bool completed) || !completed)
            return false;

        if (!TryVerifyReceiptBufferBinding(in receipt, sourceBuffer, out route))
            return false;

        bool read = TryReadBufferBytesForDiagnostics(
            _resourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                "The Vulkan backend object context is not initialized."),
            sourceBuffer,
            sourceByteOffset,
            destination,
            out route);
        return read;
    }

    /// <summary>
    /// Fails closed unless the mutable engine buffer still resolves to the exact
    /// native handle and generation recorded by the receipt's command buffer.
    /// A replacement allocation is never read through an older receipt.
    /// </summary>
    private bool TryVerifyReceiptBufferBinding(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        XRDataBuffer sourceBuffer,
        out string route)
    {
        route = "<no-generated-vulkan-buffer>";
        if (_resourceRuntime.WrapperLookup.GetOrCreate(sourceBuffer, generateNow: false) is not VkDataBuffer
            {
                IsGenerated: true,
                BufferHandle: { } buffer,
            } || buffer.Handle == 0)
        {
            return false;
        }

        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        VulkanResourceLifetimeKey key = new(ObjectType.Buffer, buffer.Handle);
        lock (tracker.SyncRoot)
        {
            if (!IsCurrentExplicitProductionReceipt(in receipt) ||
                !TryFindRecordedBufferGeneration(
                    _currentExplicitProductionResources.AsSpan(0, _currentExplicitProductionResourceCount),
                    key, out ulong recordedGeneration))
            {
                route = "<buffer-not-in-submitted-sealed-resource-vector>";
                return false;
            }
            if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
            {
                route = "<buffer-generation-not-live>";
                return false;
            }
            if (recordedGeneration != resource.Generation ||
                (resource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                route = "<buffer-binding-replaced-or-retiring>";
                return false;
            }
        }

        route = "receipt-recorded-native-binding";
        return true;
    }

    private VulkanExplicitProductionSubmissionReceipt CreateExplicitProductionReceipt(
        ulong frameNumber,
        ulong engineFrameId,
        uint expectedFrameSlot,
        ulong targetGeneration,
        CommandBuffer commandBuffer,
        ulong graphicsTimelineSignal)
        => new(
            _explicitProductionReceiptOwnerIdentity,
            _backendGeneration,
            unchecked((ulong)(nuint)_deviceContext.Device.Handle),
            frameNumber,
            engineFrameId,
            expectedFrameSlot,
            targetGeneration,
            unchecked((ulong)(nuint)commandBuffer.Handle),
            graphicsTimelineSignal);

    private bool IsCurrentExplicitProductionReceipt(in VulkanExplicitProductionSubmissionReceipt receipt)
        => IsAuthenticExplicitProductionReceipt(in receipt) &&
           receipt == _currentExplicitProductionReadbackReceipt &&
           receipt.TargetGeneration == RequireExplicitFrameTarget().TargetGeneration;

    // Completion is intentionally broader than readback: an earlier authentic
    // receipt can prove its submission completed after a later frame has been
    // accepted, which is necessary to inspect allocation overlap. The target's
    // mutable "last submitted" image/buffer remains readable only through the
    // exact latest receipt above.
    private bool IsAuthenticExplicitProductionReceipt(in VulkanExplicitProductionSubmissionReceipt receipt)
        => receipt.IsValid &&
           _lastExplicitProductionReceipt.IsValid &&
           receipt.OwnerIdentity == _explicitProductionReceiptOwnerIdentity &&
           receipt.BackendGeneration == _backendGeneration &&
           receipt.DeviceHandle == unchecked((ulong)(nuint)_deviceContext.Device.Handle) &&
           receipt.ExplicitFrameNumber <= _lastExplicitProductionReceipt.ExplicitFrameNumber &&
           receipt.GraphicsTimelineSignal <= _lastExplicitProductionReceipt.GraphicsTimelineSignal;
}
