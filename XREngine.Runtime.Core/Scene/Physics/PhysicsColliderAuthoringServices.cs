namespace XREngine.Scene.Physics;

/// <summary>Holds the optional collider-authoring service installed by editor and cook hosts.</summary>
public static class PhysicsColliderAuthoringServices
{
    private static IPhysicsColliderAuthoringService? _current;

    public static IPhysicsColliderAuthoringService? Current => Volatile.Read(ref _current);

    public static IDisposable Install(IPhysicsColliderAuthoringService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        IPhysicsColliderAuthoringService? previous = Interlocked.Exchange(ref _current, service);
        return new InstallationLease(service, previous);
    }

    public static IPhysicsColliderAuthoringService Require()
        => Current ?? throw new InvalidOperationException(
            "Collider authoring service is not installed. Use cooked collider geometry in runtime hosts, or install the CoACD authoring module in an editor or cook host.");

    private sealed class InstallationLease(
        IPhysicsColliderAuthoringService installed,
        IPhysicsColliderAuthoringService? previous) : IDisposable
    {
        private IPhysicsColliderAuthoringService? _installed = installed;

        public void Dispose()
        {
            IPhysicsColliderAuthoringService? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }
}
