using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;

namespace XREngine.Scene.Physics.Jolt
{
    // Jolt Dynamic Rigid Body
    public class JoltDynamicRigidBody : JoltRigidActor, IAbstractDynamicRigidBody, IPhysicsDynamicBodySettings, IPhysicsSceneAttachedActor
    {
        private readonly Lock _kinematicTargetLock = new();
        private (Vector3 position, Quaternion rotation)? _kinematicTarget;
        private bool _hasPendingKinematicTarget;
        private bool _kinematicMoveActive;
        private bool _gravityEnabled;

        internal JoltDynamicRigidBody(BodyID bodyId, bool gravityEnabled = true)
        {
            BodyID = bodyId;
            _gravityEnabled = gravityEnabled;
        }

        public XRComponent? OwningComponent { get; set; }

        AbstractPhysicsScene? IPhysicsSceneAttachedActor.AttachedScene => Scene;

        void IPhysicsDynamicBodySettings.SetCollisionFiltering(ushort collisionGroup, PhysicsGroupsMask groupsMask)
            => SetObjectLayer(collisionGroup, groupsMask.Word0);

        void IPhysicsDynamicBodySettings.SetMotionQuality(PhysicsRigidBodyFlags flags)
            => SetMotionQualityFromFlags(flags);

        void IPhysicsDynamicBodySettings.SetDamping(float linear, float angular)
            => SetLinearAndAngularDamping(linear, angular);

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

        /// <summary>Immediately changes pose and cancels any target waiting for the next simulation step.</summary>
        public void SetTransform(Vector3 position, Quaternion rotation, Activation activation = Activation.Activate)
        {
            if (Scene?.PhysicsSystem is null)
                return;

            lock (_kinematicTargetLock)
            {
                _kinematicTarget = null;
                _hasPendingKinematicTarget = false;
            }

            Scene.PhysicsSystem.BodyInterface.SetPositionAndRotation(BodyID, position, rotation, activation);
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

        public bool GravityEnabled
        {
            get => _gravityEnabled;
            set => SetGravityEnabled(value);
        }

        public void SetGravityEnabled(bool enabled)
        {
            _gravityEnabled = enabled;
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

            ObjectLayer layer = LayerMaskJoltExtensions.CreateObjectLayer(collisionGroup, groupsMaskWord0);
            Scene.PhysicsSystem.BodyInterface.SetObjectLayer(BodyID, layer);
        }

        /// <summary>
        /// Queues the latest target for velocity-aware motion over the next fixed simulation step.
        /// A non-null target selects kinematic motion; null restores dynamic motion on that step.
        /// </summary>
        public (Vector3 position, Quaternion rotation)? KinematicTarget
        {
            get
            {
                lock (_kinematicTargetLock)
                    return _kinematicTarget;
            }
            set
            {
                if (value is { } target)
                {
                    if (!float.IsFinite(target.position.X)
                        || !float.IsFinite(target.position.Y)
                        || !float.IsFinite(target.position.Z))
                        throw new ArgumentOutOfRangeException(nameof(value), "Kinematic target position must be finite.");

                    target.rotation = JoltShapeFactory.NormalizeRotation(target.rotation, nameof(value));
                    value = target;
                }

                // Producers may run outside the physics thread. Copy a value mailbox rather than
                // allocating a queued closure or touching the native body while it is simulating.
                lock (_kinematicTargetLock)
                {
                    _kinematicTarget = value;
                    _hasPendingKinematicTarget = true;
                }
            }
        }

        /// <summary>Consumes target commands on the scene's simulation owner before native integration.</summary>
        internal void ApplyKinematicTarget(BodyInterface bodies, float deltaSeconds)
        {
            bool hasTargetCommand;
            (Vector3 position, Quaternion rotation)? target;
            lock (_kinematicTargetLock)
            {
                hasTargetCommand = _hasPendingKinematicTarget;
                target = _kinematicTarget;
                _hasPendingKinematicTarget = false;
            }

            if (!hasTargetCommand && !_kinematicMoveActive)
                return;

            if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "Kinematic motion requires a finite, positive simulation delta.");

            MotionType motionType = bodies.GetMotionType(BodyID);
            if (hasTargetCommand && target is { } destination)
            {
                // Match the neutral property's existing PhysX behavior: supplying a target
                // explicitly selects kinematic motion, rather than imparting velocity to a dynamic body.
                if (motionType != MotionType.Kinematic)
                    bodies.SetMotionType(BodyID, MotionType.Kinematic, Activation.Activate);

                bodies.MoveKinematic(BodyID, destination.position, destination.rotation, deltaSeconds);
                _kinematicMoveActive = true;
                return;
            }

            // Jolt retains MoveKinematic's velocity. A one-shot target must not carry the body
            // past that target on subsequent steps without another command.
            if (_kinematicMoveActive && motionType == MotionType.Kinematic)
                bodies.SetLinearAndAngularVelocity(BodyID, Vector3.Zero, Vector3.Zero);
            _kinematicMoveActive = false;

            if (hasTargetCommand && motionType == MotionType.Kinematic)
                bodies.SetMotionType(BodyID, MotionType.Dynamic, Activation.Activate);
        }

        void IAbstractDynamicRigidBody.SetTransform(Vector3 position, Quaternion rotation, bool wake)
            => SetTransform(position, rotation, wake ? Activation.Activate : Activation.DontActivate);

        void IAbstractDynamicRigidBody.SetLinearVelocity(Vector3 velocity, bool wake)
        {
            SetLinearVelocity(velocity);
            if (wake)
                WakeUp();
        }

        void IAbstractDynamicRigidBody.SetAngularVelocity(Vector3 velocity, bool wake)
        {
            SetAngularVelocity(velocity);
            if (wake)
                WakeUp();
        }

        public void WakeUp()
        {
            if (Scene?.PhysicsSystem is not null)
                Scene.PhysicsSystem.BodyInterface.ActivateBody(BodyID);
        }
    }
}
