using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Physics.Physx;

namespace XREngine;

/// <summary>Installs the physics implementations included with the desktop host.</summary>
internal static class BuiltInPhysicsBackendModules
{
    public static void RegisterDesktop(PhysicsBackendCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register(new JoltPhysicsBackendModule());
        catalog.Register(new PhysxPhysicsBackendModule());
    }
}
