namespace XREngine.Components.Physics;

/// <summary>Facade boundary for optional model-derived static collider authoring.</summary>
public interface IRuntimeStaticColliderAuthoringServices
{
    void OnActivated(StaticRigidBodyComponent component);
}

public static class RuntimeStaticColliderAuthoringServices
{
    private static IRuntimeStaticColliderAuthoringServices _current = new NoopServices();

    public static IRuntimeStaticColliderAuthoringServices Current
    {
        get => _current;
        set => _current = value ?? new NoopServices();
    }

    private sealed class NoopServices : IRuntimeStaticColliderAuthoringServices
    {
        public void OnActivated(StaticRigidBodyComponent component) { }
    }
}