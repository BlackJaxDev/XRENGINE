namespace XREngine.Rendering;

/// <summary>
/// Aggregates the independently owned registrations installed by a concrete runtime module.
/// </summary>
public sealed class CompositeModuleRegistrationLease(params IDisposable[] leases) : IDisposable
{
    private IDisposable[]? _leases = leases;

    public void Dispose()
    {
        IDisposable[]? leasesToDispose = Interlocked.Exchange(ref _leases, null);
        if (leasesToDispose is null)
            return;

        for (int i = leasesToDispose.Length - 1; i >= 0; i--)
            leasesToDispose[i].Dispose();
    }
}
