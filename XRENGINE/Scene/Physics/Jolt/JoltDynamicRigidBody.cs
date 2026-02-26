using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;

namespace XREngine.Scene.Physics.Jolt
{
    // Jolt Dynamic Rigid Body
    public class JoltDynamicRigidBody : JoltRigidActor, IAbstractDynamicRigidBody
    {
        internal JoltDynamicRigidBody(BodyID bodyId)
        {
            BodyID = bodyId;
        }

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
                
                return !Scene.PhysicsSystem.BodyInterface.IsActive(BodyID);
            }
        }

        public void SetGravityEnabled(bool enabled)
        {
            if (Scene?.PhysicsSystem is null)
                return;

            Scene.PhysicsSystem.BodyInterface.SetGravityFactor(BodyID, enabled ? 1.0f : 0.0f);
        }

        public void SetMotionQualityFromFlags(PhysicsRigidBodyFlags flags)
        {
            if (Scene?.PhysicsSystem is null)
                return;

            MotionQuality quality = flags.HasFlag(PhysicsRigidBodyFlags.EnableCcd)
                ? MotionQuality.LinearCast
                : MotionQuality.Discrete;
            Scene.PhysicsSystem.BodyInterface.SetMotionQuality(BodyID, quality);
        }

        public void SetLinearAndAngularDamping(float linear, float angular)
        {
            if (Scene is null)
                return;

            Scene.WithBodyWrite(BodyID, body =>
            {
                MotionProperties mp = body.MotionProperties;
                mp.LinearDamping = linear;
                mp.AngularDamping = angular;
            });
        }

        public void SetMass(float mass)
        {
            if (Scene is null || mass <= 0.0f)
                return;

            Scene.WithBodyWrite(BodyID, body =>
            {
                MotionProperties mp = body.MotionProperties;
                mp.ScaleToMass(mass);
            });
        }

        public void SetLockFlags(PhysicsLockFlags lockFlags)
        {
            if (Scene is null)
                return;

            Scene.WithBodyWrite(BodyID, body =>
            {
                AllowedDOFs allowed = AllowedDOFs.All;
                if (lockFlags.HasFlag(PhysicsLockFlags.LinearX)) allowed &= ~AllowedDOFs.TranslationX;
                if (lockFlags.HasFlag(PhysicsLockFlags.LinearY)) allowed &= ~AllowedDOFs.TranslationY;
                if (lockFlags.HasFlag(PhysicsLockFlags.LinearZ)) allowed &= ~AllowedDOFs.TranslationZ;
                if (lockFlags.HasFlag(PhysicsLockFlags.AngularX)) allowed &= ~AllowedDOFs.RotationX;
                if (lockFlags.HasFlag(PhysicsLockFlags.AngularY)) allowed &= ~AllowedDOFs.RotationY;
                if (lockFlags.HasFlag(PhysicsLockFlags.AngularZ)) allowed &= ~AllowedDOFs.RotationZ;

                MotionProperties mp = body.MotionProperties;
                MassProperties massProps = body.Shape.MassProperties;
                mp.SetMassProperties(allowed, massProps);
            });
        }

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