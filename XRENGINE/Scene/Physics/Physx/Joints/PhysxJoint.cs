using MagicPhysX;
using System.Numerics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public abstract unsafe class PhysxJoint : IAbstractJoint
    {
        public abstract PxJoint* JointBase { get; }

        public PxScene* Scene => JointBase->GetScene();
        public void Release() => JointBase->ReleaseMut();
        public string Name
        {
            get => new((sbyte*)JointBase->GetName());
            set
            {
                fixed (char* v = value)
                    JointBase->SetNameMut((byte*)v);
            }
        }
        public PxConstraint* Constraint => JointBase->GetConstraint();

        public float InvMassScale0
        {
            get => JointBase->GetInvMassScale0();
            set => JointBase->SetInvMassScale0Mut(value);
        }

        public float InvMassScale1
        {
            get => JointBase->GetInvMassScale1();
            set => JointBase->SetInvMassScale1Mut(value);
        }

        public float InvInertiaScale0
        {
            get => JointBase->GetInvInertiaScale0();
            set => JointBase->SetInvInertiaScale0Mut(value);
        }

        public float InvInertiaScale1
        {
            get => JointBase->GetInvInertiaScale1();
            set => JointBase->SetInvInertiaScale1Mut(value);
        }

        public PxConstraintFlags Flags
        {
            get => JointBase->GetConstraintFlags();
            set => JointBase->SetConstraintFlagsMut(value);
        }

        public void SetFlag(PxConstraintFlag flag, bool value)
            => JointBase->SetConstraintFlagMut(flag, value);

        public (float force, float torque) BreakForce
        {
            get
            {
                float force, torque;
                JointBase->GetBreakForce(&force, &torque);
                return (force, torque);
            }
            set => JointBase->SetBreakForceMut(value.force, value.torque);
        }

        public Vector3 RelativeAngularVelocity => JointBase->GetRelativeAngularVelocity();
        public Vector3 RelativeLinearVelocity => JointBase->GetRelativeLinearVelocity();
        public (Vector3 position, Quaternion rotation) RelativeTransform
        {
            get
            {
                PxTransform t = JointBase->GetRelativeTransform();
                return (t.p, t.q);
            }
        }

        public (Vector3 position, Quaternion rotation) LocalPoseActor0
        {
            get
            {
                PxTransform t = JointBase->GetLocalPose(PxJointActorIndex.Actor0);
                return (t.p, t.q);
            }
            set
            {
                PxTransform v = new() { p = value.position, q = value.rotation };
                JointBase->SetLocalPoseMut(PxJointActorIndex.Actor0, &v);
            }
        }
        public (Vector3 position, Quaternion rotation) LocalPoseActor1
        {
            get
            {
                PxTransform t = JointBase->GetLocalPose(PxJointActorIndex.Actor1);
                return (t.p, t.q);
            }
            set
            {
                PxTransform v = new() { p = value.position, q = value.rotation };
                JointBase->SetLocalPoseMut(PxJointActorIndex.Actor1, &v);
            }
        }
        public void GetActors(out PxRigidActor* actor0, out PxRigidActor* actor1)
        {
            PxRigidActor* a0, a1;
            JointBase->GetActors(&a0, &a1);
            actor0 = a0;
            actor1 = a1;
        }
        public void SetActors(PxRigidActor* actor0, PxRigidActor* actor1)
            => JointBase->SetActorsMut(actor0, actor1);

        #region IAbstractJoint

        JointAnchor IAbstractJoint.LocalFrameA
        {
            get
            {
                var (p, q) = LocalPoseActor0;
                return new JointAnchor(p, q);
            }
            set => LocalPoseActor0 = (value.Position, value.Rotation);
        }

        JointAnchor IAbstractJoint.LocalFrameB
        {
            get
            {
                var (p, q) = LocalPoseActor1;
                return new JointAnchor(p, q);
            }
            set => LocalPoseActor1 = (value.Position, value.Rotation);
        }

        float IAbstractJoint.BreakForce
        {
            get => BreakForce.force;
            set => BreakForce = (value, BreakForce.torque);
        }

        float IAbstractJoint.BreakTorque
        {
            get => BreakForce.torque;
            set => BreakForce = (BreakForce.force, value);
        }

        Vector3 IAbstractJoint.RelativeLinearVelocity => RelativeLinearVelocity;
        Vector3 IAbstractJoint.RelativeAngularVelocity => RelativeAngularVelocity;

        float IAbstractJoint.InvMassScaleA { get => InvMassScale0; set => InvMassScale0 = value; }
        float IAbstractJoint.InvMassScaleB { get => InvMassScale1; set => InvMassScale1 = value; }
        float IAbstractJoint.InvInertiaScaleA { get => InvInertiaScale0; set => InvInertiaScale0 = value; }
        float IAbstractJoint.InvInertiaScaleB { get => InvInertiaScale1; set => InvInertiaScale1 = value; }

        bool IAbstractJoint.EnableCollision
        {
            get => Flags.HasFlag(PxConstraintFlags.CollisionEnabled);
            set => SetFlag(PxConstraintFlag.CollisionEnabled, value);
        }

        bool IAbstractJoint.EnablePreprocessing
        {
            get => !Flags.HasFlag(PxConstraintFlags.DisablePreprocessing);
            set => SetFlag(PxConstraintFlag.DisablePreprocessing, !value);
        }

        #endregion
    }
}