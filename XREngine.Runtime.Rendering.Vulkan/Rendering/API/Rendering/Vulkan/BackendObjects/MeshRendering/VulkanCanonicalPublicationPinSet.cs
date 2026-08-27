using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Preallocated, allocation-free ownership of one GPU pin per distinct
/// canonical scene publication. A set may move its complete ownership into an
/// accepted frame slot without reacquiring or copying disposable authority.
/// </summary>
internal sealed class VulkanCanonicalPublicationPinSet
{
    private readonly AdvancedSharedGpuSceneDatabase?[] _databases;
    private readonly AdvancedGpuScenePublicationReference[] _publications;
    private readonly AdvancedGpuScenePublicationLease[] _leases;
    private int _count;

    internal VulkanCanonicalPublicationPinSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _databases = new AdvancedSharedGpuSceneDatabase[capacity];
        _publications = new AdvancedGpuScenePublicationReference[capacity];
        _leases = new AdvancedGpuScenePublicationLease[capacity];
    }

    internal bool IsEmpty => _count == 0;

    internal bool TryRetain(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw)
    {
        if (!canonicalDraw.IsValid || canonicalDraw.Database is not { } database)
            return true;

        AdvancedGpuScenePublicationReference publication = canonicalDraw.Publication;
        for (int index = 0; index < _count; ++index)
        {
            if (ReferenceEquals(_databases[index], database) &&
                _publications[index] == publication)
            {
                return true;
            }
        }
        if (_count == _leases.Length ||
            !database.TryAcquirePublicationLease(
                publication,
                EAdvancedGpuScenePublicationPinKind.Gpu,
                out AdvancedGpuScenePublicationLease lease))
        {
            return false;
        }

        int slot = _count++;
        _databases[slot] = database;
        _publications[slot] = publication;
        _leases[slot] = lease;
        return true;
    }

    /// <summary>Moves all pin ownership into an already-retired empty set.</summary>
    internal void MoveTo(VulkanCanonicalPublicationPinSet destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.IsEmpty)
        {
            throw new InvalidOperationException(
                "Canonical publication pin destination was not retired before transfer.");
        }

        for (int index = 0; index < _count; ++index)
        {
            destination._databases[index] = _databases[index];
            destination._publications[index] = _publications[index];
            destination._leases[index] = _leases[index];
            _databases[index] = null;
            _publications[index] = default;
            _leases[index] = default;
        }
        destination._count = _count;
        _count = 0;
    }

    internal void ReleaseAll()
    {
        for (int index = 0; index < _count; ++index)
        {
            _leases[index].Dispose();
            _databases[index] = null;
            _publications[index] = default;
            _leases[index] = default;
        }
        _count = 0;
    }

    internal void ReleaseMatching(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw)
    {
        AdvancedSharedGpuSceneDatabase? database = canonicalDraw.Database;
        AdvancedGpuScenePublicationReference publication = canonicalDraw.Publication;
        for (int index = 0; index < _count; ++index)
        {
            if (!ReferenceEquals(_databases[index], database) ||
                _publications[index] != publication)
            {
                continue;
            }

            _leases[index].Dispose();
            int last = --_count;
            if (index != last)
            {
                _databases[index] = _databases[last];
                _publications[index] = _publications[last];
                _leases[index] = _leases[last];
            }
            _databases[last] = null;
            _publications[last] = default;
            _leases[last] = default;
            return;
        }
    }
}
