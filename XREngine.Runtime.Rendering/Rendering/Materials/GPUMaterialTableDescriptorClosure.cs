using System.Threading;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Shared immutable descriptor-element ownership for material rows. Scalar-only
/// publications retain this closure without reacquiring native descriptor slots.
/// Backend leases must defer their final cleanup when disposal occurs under a
/// command-lifetime lock.
/// </summary>
public sealed class GPUMaterialTableDescriptorClosure : IDisposable
{
    private readonly GPUMaterialTextureReference[] _references;
    private readonly object _registrationGate = new();
    private object? _backendOwner;
    private IDisposable? _backendLease;
    private int _referenceCount = 1;

    internal GPUMaterialTableDescriptorClosure(
        GPUMaterialTextureReference[] references, ulong ownerId, ulong generation)
        => (_references, OwnerId, Generation) = (references, ownerId, generation);

    public ulong OwnerId { get; }
    public ulong Generation { get; }
    public ReadOnlySpan<GPUMaterialTextureReference> References => _references;

    public GPUMaterialTableDescriptorClosure Retain()
    {
        while (true)
        {
            int count = Volatile.Read(ref _referenceCount);
            if (count <= 0)
                throw new ObjectDisposedException(nameof(GPUMaterialTableDescriptorClosure));
            if (count == int.MaxValue)
                throw new InvalidOperationException("Material descriptor closure reference count overflow.");
            if (Interlocked.CompareExchange(ref _referenceCount, count + 1, count) == count)
                return this;
        }
    }

    /// <summary>Returns a borrowed lease while the caller retains this closure.</summary>
    public bool TryGetBackendLease(object backendOwner, out IDisposable? lease)
    {
        ArgumentNullException.ThrowIfNull(backendOwner);
        lock (_registrationGate)
        {
            lease = Volatile.Read(ref _referenceCount) > 0 && ReferenceEquals(_backendOwner, backendOwner)
                ? _backendLease : null;
            return lease is not null;
        }
    }

    /// <summary>
    /// Registers exactly one backend owner for these renderer-local descriptor
    /// indices. The caller disposes its candidate unless registeredLease is that
    /// exact object, including when another caller already registered a lease.
    /// </summary>
    public bool TryAttachBackendLease(object backendOwner, IDisposable candidate, out IDisposable? registeredLease)
    {
        ArgumentNullException.ThrowIfNull(backendOwner);
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_registrationGate)
        {
            registeredLease = null;
            if (Volatile.Read(ref _referenceCount) <= 0 ||
                (_backendOwner is not null && !ReferenceEquals(_backendOwner, backendOwner)))
                return false;
            if (_backendLease is null)
            {
                _backendOwner = backendOwner;
                _backendLease = candidate;
            }
            registeredLease = _backendLease;
            return true;
        }
    }

    public void Dispose()
    {
        int count = Interlocked.Decrement(ref _referenceCount);
        if (count > 0)
            return;
        if (count < 0)
        {
            Interlocked.Increment(ref _referenceCount);
            throw new InvalidOperationException("Material descriptor closure reference count underflow.");
        }
        IDisposable? lease;
        lock (_registrationGate)
        {
            lease = _backendLease;
            _backendLease = null;
            _backendOwner = null;
        }
        // Never invoke backend cleanup under the registration lock. A backend
        // lease enqueues cleanup so native command-reset locks remain acyclic.
        lease?.Dispose();
    }
}
