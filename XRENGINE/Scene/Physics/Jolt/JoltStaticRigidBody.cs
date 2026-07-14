using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;

namespace XREngine.Scene.Physics.Jolt
{
    // Jolt Static Rigid Body
    public class JoltStaticRigidBody : JoltRigidActor, IAbstractStaticRigidBody
    {
        internal JoltStaticRigidBody(BodyID bodyId)
        {
            BodyID = bodyId;
        }

        private StaticRigidBodyComponent? _owningComponent;
        public StaticRigidBodyComponent? OwningComponent
        {
            get => _owningComponent;
            set => _owningComponent = value;
        }

        public override XRComponent? GetOwningComponent() => OwningComponent;

        public override (Vector3 position, Quaternion rotation) Transform
        {
            get
            {
                if (Scene?.PhysicsSystem is null)
                    return (Vector3.Zero, Quaternion.Identity);

                BodyInterface bodies = Scene.PhysicsSystem.BodyInterface;
                return (bodies.GetPosition(BodyID), bodies.GetRotation(BodyID));
            }
        }

        public override Vector3 LinearVelocity => Vector3.Zero;
        public override Vector3 AngularVelocity => Vector3.Zero;
        public override bool IsSleeping => true;

        public void SetObjectLayer(ushort collisionGroup, uint groupsMaskWord0)
        {
            if (Scene?.PhysicsSystem is null)
                return;

            ObjectLayer layer = LayerMaskJoltExtensions.CreateObjectLayer(collisionGroup, groupsMaskWord0);
            Scene.PhysicsSystem.BodyInterface.SetObjectLayer(BodyID, layer);
        }
    }
} 
