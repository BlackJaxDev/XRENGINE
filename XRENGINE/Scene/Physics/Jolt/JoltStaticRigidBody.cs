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

                Matrix4x4 transform = Scene.PhysicsSystem.BodyInterface.GetWorldTransform(BodyID);
                Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out Vector3 position);
                return (position, rotation);
            }
        }

        public override Vector3 LinearVelocity => Vector3.Zero;
        public override Vector3 AngularVelocity => Vector3.Zero;
        public override bool IsSleeping => true;

        public void SetObjectLayer(ushort collisionGroup, uint groupsMaskWord0)
        {
            if (Scene?.PhysicsSystem is null)
                return;

            uint mask = groupsMaskWord0 == 0 ? uint.MaxValue : groupsMaskWord0;
            ObjectLayer layer = ObjectLayerPairFilterMask.GetObjectLayer(collisionGroup, mask);
            Scene.PhysicsSystem.BodyInterface.SetObjectLayer(BodyID, layer);
        }
    }
} 