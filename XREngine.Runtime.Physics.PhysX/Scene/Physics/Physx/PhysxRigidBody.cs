using MagicPhysX;
using System.Numerics;
using static MagicPhysX.NativeMethods;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe abstract class PhysxRigidBody : PhysxRigidActor
    {
        public abstract PxRigidBody* BodyPtr { get; }
        public override unsafe PxRigidActor* RigidActorPtr => (PxRigidActor*)BodyPtr;

        public Dictionary<nint, PhysxRigidBody> AllRigidBodies { get; } = [];
        public PhysxRigidBody? Get(PxRigidBody* ptr)
            => AllRigidBodies.TryGetValue((nint)ptr, out var body) ? body : null;

        public float AngularDamping
        {
            get => PxRigidBody_getAngularDamping(BodyPtr);
            set => PxRigidBody_setAngularDamping_mut(BodyPtr, value);
        }
        public float LinearDamping
        {
            get => PxRigidBody_getLinearDamping(BodyPtr);
            set => PxRigidBody_setLinearDamping_mut(BodyPtr, value);
        }
        public float MaxAngularVelocity
        {
            get => PxRigidBody_getMaxAngularVelocity(BodyPtr);
            set => PxRigidBody_setMaxAngularVelocity_mut(BodyPtr, value);
        }
        public float MaxLinearVelocity
        {
            get => PxRigidBody_getMaxLinearVelocity(BodyPtr);
            set => PxRigidBody_setMaxLinearVelocity_mut(BodyPtr, value);
        }
        public override Vector3 LinearVelocity
            => PxRigidBody_getLinearVelocity(BodyPtr);

        public override Vector3 AngularVelocity
            => PxRigidBody_getAngularVelocity(BodyPtr);

        public (Quaternion, Vector3) CMassLocalPose
        {
            get
            {
                var tfm = PxRigidBody_getCMassLocalPose(BodyPtr);
                return (tfm.q, tfm.p);
            }
            set
            {
                var x = new PxTransform { q = value.Item1, p = value.Item2 };
                PxRigidBody_setCMassLocalPose_mut(BodyPtr, &x);
            }
        }
        public float Mass
        {
            get => PxRigidBody_getMass(BodyPtr);
            set => PxRigidBody_setMass_mut(BodyPtr, value);
        }
        public float InvMass => PxRigidBody_getInvMass(BodyPtr);
        public Vector3 MassSpaceInertiaTensor
        {
            get => PxRigidBody_getMassSpaceInertiaTensor(BodyPtr);
            set
            {
                PxVec3 v = value;
                PxRigidBody_setMassSpaceInertiaTensor_mut(BodyPtr, &v);
            }
        }
        public Vector3 MassSpaceInvInertiaTensor => PxRigidBody_getMassSpaceInvInertiaTensor(BodyPtr);
        public void AddForce(Vector3 force, PxForceMode mode, bool wake)
        {
            PxVec3 v = force;
            PxRigidBody_addForce_mut(BodyPtr, &v, mode, wake);
        }

        public void AddForceAtPosition(Vector3 force, Vector3 position, PxForceMode mode, bool wake)
        {
            PxVec3 f = force;
            PxVec3 p = position;
            PxRigidBodyExt_addForceAtPos(BodyPtr, &f, &p, mode, wake);
        }

        public void AddForceAtLocalPosition(Vector3 force, Vector3 position, PxForceMode mode, bool wake)
        {
            PxVec3 f = force;
            PxVec3 p = position;
            PxRigidBodyExt_addForceAtLocalPos(BodyPtr, &f, &p, mode, wake);
        }

        public void AddLocalForceAtPosition(Vector3 force, Vector3 position, PxForceMode mode, bool wake)
        {
            PxVec3 f = force;
            PxVec3 p = position;
            PxRigidBodyExt_addLocalForceAtPos(BodyPtr, &f, &p, mode, wake);
        }

        public void AddLocalForceAtLocalPosition(Vector3 force, Vector3 position, PxForceMode mode, bool wake)
        {
            PxVec3 f = force;
            PxVec3 p = position;
            PxRigidBodyExt_addLocalForceAtLocalPos(BodyPtr, &f, &p, mode, wake);
        }

        public void AddTorque(Vector3 torque, PxForceMode mode, bool wake)
        {
            PxVec3 v = torque;
            PxRigidBody_addTorque_mut(BodyPtr, &v, mode, wake);
        }

        public void ClearTorque(PxForceMode mode)
            => PxRigidBody_clearTorque_mut(BodyPtr, mode);

        public void ClearForce(PxForceMode mode)
            => PxRigidBody_clearForce_mut(BodyPtr, mode);

        public void SetForceAndTorque(Vector3 force, Vector3 torque, PxForceMode mode)
        {
            PxVec3 f = force;
            PxVec3 t = torque;
            PxRigidBody_setForceAndTorque_mut(BodyPtr, &f, &t, mode);
        }

        public PxRigidBodyFlags Flags
        {
            get => PxRigidBody_getRigidBodyFlags(BodyPtr);
            set => PxRigidBody_setRigidBodyFlags_mut(BodyPtr, value);
        }

        public float MinCCDAdvanceCoefficient
        {
            get => PxRigidBody_getMinCCDAdvanceCoefficient(BodyPtr);
            set => PxRigidBody_setMinCCDAdvanceCoefficient_mut(BodyPtr, value);
        }

        public float MaxDepenetrationVelocity
        {
            get => PxRigidBody_getMaxDepenetrationVelocity(BodyPtr);
            set => PxRigidBody_setMaxDepenetrationVelocity_mut(BodyPtr, value);
        }

        public float MaxContactImpulse
        {
            get => PxRigidBody_getMaxContactImpulse(BodyPtr);
            set => PxRigidBody_setMaxContactImpulse_mut(BodyPtr, value);
        }

        public float ContactSlopCoefficient
        {
            get => PxRigidBody_getContactSlopCoefficient(BodyPtr);
            set => PxRigidBody_setContactSlopCoefficient_mut(BodyPtr, value);
        }

        public PxNodeIndex NodeIndex => PxRigidBody_getInternalIslandNodeIndex(BodyPtr);
    }
}