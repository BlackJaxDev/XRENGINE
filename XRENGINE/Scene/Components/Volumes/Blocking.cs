using System.Numerics;
using XREngine.Data.Transforms.Rotations;
using XREngine.Components.Physics;
using XREngine.Rendering.Physics.Physx;
using System.ComponentModel;

namespace XREngine.Components.Scene.Volumes
{
    /// <summary>
    /// A volume that blocks all dynamic rigid bodies from passing through it.
    /// </summary>
    [Description("A volume that blocks all dynamic rigid bodies from passing through it.")]
    public class BlockingVolumeComponent : StaticRigidBodyComponent
    {
        public BlockingVolumeComponent()
            : this(new Vector3(0.5f), 0, 0) { }
        public BlockingVolumeComponent(Vector3 halfExtents, ushort collisionGroup, ushort collidesWith)
        {
            Geometry = new IPhysicsGeometry.Box(halfExtents);
            CollisionGroup = collisionGroup;
            GroupsMask = new PhysicsGroupsMask(collidesWith, 0, 0, 0);
            GravityEnabled = false;
            SimulationEnabled = true;
        }
    }
}
