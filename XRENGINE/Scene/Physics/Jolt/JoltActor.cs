using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Scene.Physics.Jolt
{
    // Jolt Actor base class
    public abstract class JoltActor : IAbstractPhysicsActor
    {
        public BodyID BodyID { get; protected set; }
        public JoltScene? Scene { get; private set; }

        public virtual void Destroy(bool wakeOnLostTouch = false)
        {
            Scene?.RemoveActor(this);
        }

        internal virtual void OnAddedToScene(JoltScene scene)
        {
            Scene = scene;
        }

        internal virtual void OnRemovedFromScene(JoltScene scene)
        {
            Scene = null;
        }

        public abstract XRComponent? GetOwningComponent();
    }

    // Jolt Rigid Actor base class
    public abstract class JoltRigidActor : JoltActor, IAbstractRigidPhysicsActor
    {
        public abstract (Vector3 position, Quaternion rotation) Transform { get; }
        public abstract Vector3 LinearVelocity { get; }
        public abstract Vector3 AngularVelocity { get; }
        public abstract bool IsSleeping { get; }
    }
} 