namespace XREngine.Data;

/// <summary>
/// Collects reversible registrations and unwinds them in reverse order. Construction is
/// transactional: if any installation step fails, every previously added lease is disposed.
/// </summary>
public sealed class RegistrationLeaseGroup : IDisposable
{
    private List<IDisposable>? _leases = [];

    /// <summary>Runs a registration transaction and returns its aggregate lease.</summary>
    public static IDisposable Create(Action<RegistrationLeaseGroup> install)
    {
        ArgumentNullException.ThrowIfNull(install);

        RegistrationLeaseGroup group = new();
        try
        {
            install(group);
            return group;
        }
        catch (Exception installException)
        {
            try
            {
                group.Dispose();
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Registration failed and one or more completed registrations also failed to roll back.",
                    installException,
                    rollbackException);
            }

            throw;
        }
    }

    /// <summary>Adds one completed registration to the transaction.</summary>
    public void Add(IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        List<IDisposable>? leases = _leases;
        if (leases is null)
        {
            lease.Dispose();
            throw new ObjectDisposedException(nameof(RegistrationLeaseGroup));
        }

        try
        {
            leases.Add(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        List<IDisposable>? current = Interlocked.Exchange(ref _leases, null);
        if (current is null)
            return;

        List<Exception>? failures = null;
        for (int index = current.Count - 1; index >= 0; index--)
        {
            try
            {
                current[index].Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more registration leases failed to dispose.", failures);
    }
}
