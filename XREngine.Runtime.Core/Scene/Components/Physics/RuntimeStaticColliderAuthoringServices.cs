namespace XREngine.Components.Physics;

/// <summary>Facade boundary for optional model-derived static collider authoring.</summary>
public interface IRuntimeStaticColliderAuthoringServices
{
    void OnActivated(StaticRigidBodyComponent component);
}

public static class RuntimeStaticColliderAuthoringServices
{
    private static readonly IRuntimeStaticColliderAuthoringServices Default = new NoopServices();
    private static IRuntimeStaticColliderAuthoringServices _current = Default;

    public static IRuntimeStaticColliderAuthoringServices Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? Default);
    }

    public static IDisposable Install(IRuntimeStaticColliderAuthoringServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimeStaticColliderAuthoringServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        IRuntimeStaticColliderAuthoringServices installed,
        IRuntimeStaticColliderAuthoringServices previous) : IDisposable
    {
        private IRuntimeStaticColliderAuthoringServices? _installed = installed;

        public void Dispose()
        {
            IRuntimeStaticColliderAuthoringServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }

    private sealed class NoopServices : IRuntimeStaticColliderAuthoringServices
    {
        public void OnActivated(StaticRigidBodyComponent component) { }
    }
}
