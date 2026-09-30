using XREngine.Components.Physics;
using XREngine.Data.Tools;

namespace XREngine.Scene.Physics;

/// <summary>Optional backend path for attaching generated convex hulls to a static body.</summary>
public interface IPhysicsConvexHullInstaller
{
    void PrepareAndAttach(StaticRigidBodyComponent component, IReadOnlyList<CoACD.ConvexHullMesh> hulls);
}
