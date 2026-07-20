using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;

namespace XREngine.Scene.Physics.Jolt
{
    // Jolt Static Rigid Body
    public class JoltStaticRigidBody : JoltRigidActor, IAbstractStaticRigidBody, IStaticRigidBodySettingsSink
    {
        internal JoltStaticRigidBody(BodyID bodyId)
        {
            BodyID = bodyId;
        }

        public XRComponent? OwningComponent { get; set; }

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

        public void ApplyStaticRigidBodySettings(in StaticRigidBodyRuntimeSettings settings)
            => SetObjectLayer(settings.CollisionGroup, settings.GroupsMask.Word0);

        public void SetObjectLayer(ushort collisionGroup, uint groupsMaskWord0)
        {
            if (Scene?.PhysicsSystem is null)
                return;

            ObjectLayer layer = LayerMaskJoltExtensions.CreateObjectLayer(collisionGroup, groupsMaskWord0);
            Scene.PhysicsSystem.BodyInterface.SetObjectLayer(BodyID, layer);
        }
    }
} 
