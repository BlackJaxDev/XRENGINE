using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanDescriptorManager
{
    private readonly object _releasedMaterialClosuresGate = new();
    private VulkanMaterialDescriptorClosureLease? _releasedMaterialClosuresHead;
    private VulkanMaterialDescriptorClosureLease? _releasedMaterialClosuresTail;
    private long _materialClosureAcquires;
    private long _materialClosureReleases;

    internal (ulong Writes, ulong Retirements, long Acquires, long Releases, int LiveSlots, int LeasedSlots)
        SnapshotMaterialDescriptorDiagnostics()
    {
        var state = BindlessMaterialTextures;
        lock (state.Sync)
        {
            int live = 0, leased = 0;
            for (int index = 1; index < state.Slots.Length; ++index)
            {
                if (state.Slots[index].Texture is not null)
                    live++;
                if (state.Slots[index].LeaseCount != 0)
                    leased++;
            }
            return (state.WritesTotal, state.SlotRetirementsTotal,
                Interlocked.Read(ref _materialClosureAcquires), Interlocked.Read(ref _materialClosureReleases), live, leased);
        }
    }

    /// <summary>Registers native ownership once, while authoring still owns the immutable token.</summary>
    internal bool TryEnsureMaterialPublicationClosure(GPUMaterialTablePublication publication, out string reason)
    {
        GPUMaterialTableDescriptorClosure closure = publication.DescriptorClosure;
        if (closure.TryGetBackendLease(this, out _))
        {
            reason = string.Empty;
            return true;
        }
        if (!VulkanMaterialDescriptorClosureLease.TryAcquire(this, publication, out var candidate, out reason))
            return false;
        IDisposable? registered = null;
        try
        {
            if (closure.TryAttachBackendLease(this, candidate!, out registered))
            {
                if (ReferenceEquals(registered, candidate))
                    Interlocked.Increment(ref _materialClosureAcquires);
                return true;
            }
            reason = "Material descriptor indices belong to a different or expired Vulkan resource authority.";
            return false;
        }
        finally
        {
            if (!ReferenceEquals(registered, candidate))
                candidate!.Dispose();
        }
    }

    internal void EnqueueReleasedMaterialDescriptorClosure(VulkanMaterialDescriptorClosureLease closure)
    {
        lock (_releasedMaterialClosuresGate)
        {
            if (_releasedMaterialClosuresTail is null)
                _releasedMaterialClosuresHead = closure;
            else
                _releasedMaterialClosuresTail.NextReleased = closure;
            _releasedMaterialClosuresTail = closure;
        }
    }

    /// <summary>Drains without taking descriptor locks under the queue or command tracker lock.</summary>
    internal int DrainReleasedMaterialDescriptorClosures(int maxItems = 64)
    {
        int released = 0;
        while (released < maxItems)
        {
            VulkanMaterialDescriptorClosureLease? closure;
            lock (_releasedMaterialClosuresGate)
            {
                closure = _releasedMaterialClosuresHead;
                if (closure is null)
                    break;
                _releasedMaterialClosuresHead = closure.NextReleased;
                closure.NextReleased = null;
                if (_releasedMaterialClosuresHead is null)
                    _releasedMaterialClosuresTail = null;
            }
            closure.ReleaseReceipts();
            Interlocked.Increment(ref _materialClosureReleases);
            released++;
        }
        return released;
    }
}
