namespace XREngine.Core.Files;

/// <summary>
/// Deterministic installation point for upper-runtime object lifecycle hooks used by cooked
/// reflection deserialization.
/// </summary>
public static class CookedBinaryObjectLifecycleServices
{
    private static readonly ICookedBinaryObjectLifecycleServices Default = new NoOpServices();
    private static ICookedBinaryObjectLifecycleServices _current = Default;

    public static ICookedBinaryObjectLifecycleServices Current => Volatile.Read(ref _current);

    public static IDisposable Install(ICookedBinaryObjectLifecycleServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ICookedBinaryObjectLifecycleServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        ICookedBinaryObjectLifecycleServices installed,
        ICookedBinaryObjectLifecycleServices previous) : IDisposable
    {
        private ICookedBinaryObjectLifecycleServices? _installed = installed;

        public void Dispose()
        {
            ICookedBinaryObjectLifecycleServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }

    private sealed class NoOpServices : ICookedBinaryObjectLifecycleServices
    {
        public void PrepareInstance(object instance)
        {
        }

        public IDisposable? EnterMemberScope(object instance, string memberName)
            => null;
    }
}
