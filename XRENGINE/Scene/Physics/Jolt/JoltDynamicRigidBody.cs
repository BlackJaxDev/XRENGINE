using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;

namespace XREngine.Scene.Physics.Jolt
{
    // Jolt Dynamic Rigid Body
    public class JoltDynamicRigidBody : JoltRigidActor, IAbstractDynamicRigidBody
    {
        private DynamicRigidBodyComponent? _owningComponent;
        public DynamicRigidBodyComponent? OwningComponent
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

        public void SetTransform(Vector3 position, Quaternion rotation, Activation activation = Activation.Activate)
        {
            if (Scene?.PhysicsSystem is null)
                return;

            Scene.PhysicsSystem.BodyInterface.SetRPositionAndRotation(BodyID, position, rotation, activation);
        }

        public override Vector3 LinearVelocity
        {
            get
            {
                if (Scene?.PhysicsSystem is null)
                    return Vector3.Zero;
                
                return Scene.PhysicsSystem.BodyInterface.GetLinearVelocity(BodyID);
            }
        }

        public void SetLinearVelocity(Vector3 value)
        {
            if (Scene?.PhysicsSystem is null)
                return;
            Scene.PhysicsSystem.BodyInterface.SetLinearVelocity(BodyID, value);
        }

        public override Vector3 AngularVelocity
        {
            get
            {
                if (Scene?.PhysicsSystem is null)
                    return Vector3.Zero;
                
                return Scene.PhysicsSystem.BodyInterface.GetAngularVelocity(BodyID);
            }
        }

        public void SetAngularVelocity(Vector3 value)
        {
            if (Scene?.PhysicsSystem is null)
                return;
            Scene.PhysicsSystem.BodyInterface.SetAngularVelocity(BodyID, value);
        }

        public override bool IsSleeping
        {
            get
            {
                if (Scene?.PhysicsSystem is null)
                    return true;
                
                return Scene.PhysicsSystem.BodyInterface.GetMotionType(BodyID) == MotionType.Static;
            }
        }
    }
} 